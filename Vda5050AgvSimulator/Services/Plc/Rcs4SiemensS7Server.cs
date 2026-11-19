#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HslCommunication.Profinet.Siemens;

namespace Vda5050AgvSimulator.Services.Plc;

/// <summary>
/// 使用 HSL 的本地内存，独立处理 ISO-on-TCP 和经典 S7comm 报文。
/// </summary>
internal sealed class Rcs4SiemensS7Server : SiemensS7Server
{
    private const int TpktHeaderLength = 4;
    private const int CotpDataHeaderLength = 3;
    private const int S7HeaderLength = 10;
    private const int S7AckHeaderLength = 12;
    private const int MaxTpduSize = ushort.MaxValue - TpktHeaderLength;
    private const int MaxPduSize = 960;
    private const int MinPduSize = 240;
    private const int DefaultTimeoutMs = 3000;
    private const int FirstListenerRetryMs = 1000;
    private const int MaxListenerRetryMs = 10000;
    private const int MaxConsecutiveAcceptErrors = 3;

    private const byte Job = 0x01;
    private const byte AckData = 0x03;
    private const byte UserData = 0x07;
    private const byte ReadVar = 0x04;
    private const byte WriteVar = 0x05;
    private const byte SetupCommunication = 0xF0;

    private const byte Success = 0xFF;
    private const byte AddressOutOfRange = 0x05;
    private const byte UnsupportedType = 0x06;
    private const byte DataTypeMismatch = 0x07;
    private const byte ObjectNotFound = 0x0A;
    private const ushort InvalidParameter = 0x0112;
    private const ushort FunctionNotSupported = 0x8104;
    private const ushort PduSizeError = 0x8500;

    private const byte Bit = 0x03;
    private const byte ByteWordDword = 0x04;
    private const byte Integer = 0x05;
    private const byte DoubleInteger = 0x06;
    private const byte Real = 0x07;
    private const byte OctetString = 0x09;

    private static readonly byte[] EmptyBytes = Array.Empty<byte>();

    private readonly Dictionary<int, int> _dbLengths = new Dictionary<int, int>();
    private readonly object _networkMemoryLock = new object();
    private readonly Action<string> _log;
    private readonly string _cpuName;
    private readonly string _orderNumber;
    private readonly int _handshakeTimeoutMs;
    private readonly int _frameTimeoutMs;

    private CancellationTokenSource _transportCancellation;
    private Task _transportTask;
    private volatile bool _isListening;
    private int _keepAliveWarningLogged;
    private int _activeTcpConnectionCount;

    internal Rcs4SiemensS7Server(string plcType, Action<string> log,
        int connectTimeoutMs = DefaultTimeoutMs, int receiveTimeoutMs = DefaultTimeoutMs)
    {
        _log = log;
        _handshakeTimeoutMs = connectTimeoutMs > 0 ? connectTimeoutMs : DefaultTimeoutMs;
        _frameTimeoutMs = receiveTimeoutMs > 0 ? receiveTimeoutMs : DefaultTimeoutMs;
        var type = plcType?.Trim() ?? string.Empty;
        if (type.IndexOf("1500", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _cpuName = "CPU 1511-1 PN";
            _orderNumber = "6ES7 511-1AK02-0AB0";
        }
        else if (type.IndexOf("300", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _cpuName = "CPU 315-2 PN/DP";
            _orderNumber = "6ES7 315-2EH14-0AB0";
        }
        else if (type.IndexOf("400", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _cpuName = "CPU 414-3 PN/DP";
            _orderNumber = "6ES7 414-3EM07-0AB0";
        }
        else
        {
            _cpuName = "CPU 1214C";
            _orderNumber = "6ES7 214-1AG40-0XB0";
        }
    }

    internal IReadOnlyDictionary<int, int> DbLengths => _dbLengths;

    internal bool IsTransportListening => _isListening;

    internal int ActiveTcpConnectionCount => Volatile.Read(ref _activeTcpConnectionCount);

    internal void RegisterDbLength(int dbNumber, int length)
    {
        _dbLengths.Add(dbNumber, length);
    }

    internal void StartTransport(int port, IPAddress listenAddress = null)
    {
        var listener = OpenListener(port, listenAddress);
        _transportCancellation = new CancellationTokenSource();
        _isListening = true;
        _transportTask = RunTransportAsync(listener, port, _transportCancellation.Token, listenAddress);
    }

    internal void StopTransport()
    {
        try
        {
            using (var cancellation = Interlocked.Exchange(ref _transportCancellation, null))
            {
                if (cancellation != null)
                {
                    cancellation.Cancel();
                    _transportTask.GetAwaiter().GetResult();
                }
            }
        }
        finally
        {
            ServerClose();
        }
    }

    private static TcpListener OpenListener(int port, IPAddress listenAddress)
    {
        var listener = new TcpListener(listenAddress ?? IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return listener;
        }
        catch
        {
            listener.Stop();
            throw;
        }
    }

    private async Task RunTransportAsync(TcpListener listener, int port, CancellationToken stopping,
        IPAddress listenAddress)
    {
        int retryDelay = FirstListenerRetryMs;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    if (listener == null)
                    {
                        listener = OpenListener(port, listenAddress);
                        _isListening = true;
                        retryDelay = FirstListenerRetryMs;
                        _log?.Invoke($"S7 监听已恢复 {listenAddress?.ToString() ?? "127.0.0.1"}:{port}。");
                    }

                    await AcceptClientsAsync(listener, stopping).ConfigureAwait(false);
                }
                catch (Exception ex) when (stopping.IsCancellationRequested &&
                                           (ex is OperationCanceledException || ex is InvalidOperationException ||
                                            ex is SocketException))
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"S7 监听中断 {listenAddress?.ToString() ?? "127.0.0.1"}:{port}，{retryDelay} ms 后重试。{DescribeException(ex)}");
                }
                finally
                {
                    _isListening = false;
                    listener = null;
                }

                await Task.Delay(retryDelay, stopping).ConfigureAwait(false);
                retryDelay = Math.Min(retryDelay * 2, MaxListenerRetryMs);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        finally
        {
            _isListening = false;
            listener?.Stop();
        }
    }

    private async Task AcceptClientsAsync(TcpListener listener, CancellationToken stopping)
    {
        int listenerClosed = 0;
        Action closeListener = () =>
        {
            if (Interlocked.Exchange(ref listenerClosed, 1) == 0)
            {
                listener.Stop();
            }
        };

        // Framework 通过关闭监听套接字结束等待中的 Accept。
        using (var sessionsCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        using (stopping.Register(closeListener))
        {
            var sessions = new List<Task>();
            int consecutiveErrors = 0;
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    }
                    catch (SocketException ex) when (!stopping.IsCancellationRequested &&
                                                     (ex.SocketErrorCode == SocketError.ConnectionReset ||
                                                      ex.SocketErrorCode == SocketError.ConnectionAborted))
                    {
                        // 对端在 Accept 完成前撤销连接，原监听仍可继续使用。
                        if (++consecutiveErrors >= MaxConsecutiveAcceptErrors)
                        {
                            throw;
                        }

                        await Task.Delay(100, stopping).ConfigureAwait(false);
                        continue;
                    }

                    consecutiveErrors = 0;
                    sessions.RemoveAll(task => task.IsCompleted);
                    sessions.Add(ServeClientAsync(client, sessionsCancellation.Token));
                }
            }
            finally
            {
                _isListening = false;
                try
                {
                    closeListener();
                }
                finally
                {
                    sessionsCancellation.Cancel();
                    await Task.WhenAll(sessions).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken stopping)
    {
        using (client)
        using (var session = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        using (session.Token.Register(client.Close))
        {
            int pduSize = 0;
            bool cotpConfirmed = false;
            bool handshakeComplete = false;
            string peer = "未知";
            string phase = "COTP 握手";
            Interlocked.Increment(ref _activeTcpConnectionCount);
            try
            {
                session.Token.ThrowIfCancellationRequested();
                peer = client.Client.RemoteEndPoint?.ToString() ?? peer;
                client.NoDelay = true;
                EnableTcpKeepAlive(client.Client);
                session.CancelAfter(_handshakeTimeoutMs);

                using (var stream = client.GetStream())
                using (var request = new MemoryStream(MaxPduSize))
                {
                    var tpdu = await ReadTpktAsync(stream, 255, session, 0).ConfigureAwait(false);
                    if (!TryConfirmCotp(tpdu, out var confirmation, out var tpduSize))
                    {
                        _log?.Invoke($"S7 COTP 握手请求无效，对端 {peer}，TPDU 类型 0x{tpdu[1]:X2}，长度 {tpdu.Length}。");
                        return;
                    }

                    await WriteTpktAsync(stream, confirmation, session.Token).ConfigureAwait(false);
                    cotpConfirmed = true;
                    phase = "S7 参数协商";

                    bool startsNewPdu = true;
                    while (!session.IsCancellationRequested)
                    {
                        // 空闲连接等待首字节，收到请求后才开始计算本次收发的时限。
                        int timeoutOnFirstByte = handshakeComplete && startsNewPdu ? _frameTimeoutMs : 0;
                        tpdu = await ReadTpktAsync(stream, tpduSize, session, timeoutOnFirstByte).ConfigureAwait(false);
                        if (tpdu[1] == 0x80)
                        {
                            if (!handshakeComplete)
                            {
                                _log?.Invoke($"S7 参数协商中断，对端 {peer} 发送了 COTP 断开请求。");
                            }

                            return;
                        }

                        if (tpdu[0] != 2 || tpdu[1] != 0xF0 ||
                            request.Length + tpdu.Length - CotpDataHeaderLength > MaxTpduSize - CotpDataHeaderLength)
                        {
                            _log?.Invoke($"S7 COTP 数据帧格式或长度不正确，对端 {peer}。");
                            return;
                        }

                        request.Write(tpdu, CotpDataHeaderLength, tpdu.Length - CotpDataHeaderLength);
                        startsNewPdu = (tpdu[2] & 0x80) != 0;
                        if (!startsNewPdu)
                        {
                            continue;
                        }

                        int requestLength = (int)request.Length;
                        var response = ProcessS7Request(request.GetBuffer(), requestLength, ref pduSize);
                        request.SetLength(0);
                        if (!handshakeComplete && pduSize == 0)
                        {
                            if (response.Length == 0)
                            {
                                _log?.Invoke($"S7 参数协商请求无效，对端 {peer}，PDU 长度 {requestLength}。");
                            }
                            else
                            {
                                var error = ReadUInt16BigEndian(response, S7HeaderLength);
                                _log?.Invoke($"S7 参数协商请求被拒绝，对端 {peer}，S7 错误码 0x{error:X4}。");
                            }
                        }

                        if (response.Length != 0)
                        {
                            await WriteS7Async(stream, response, tpduSize, session.Token).ConfigureAwait(false);
                        }

                        if (pduSize > 0)
                        {
                            session.CancelAfter(Timeout.Infinite);
                            if (!handshakeComplete)
                            {
                                handshakeComplete = true;
                                phase = "S7 报文收发";
                                _log?.Invoke($"S7 会话已建立，对端 {peer}，PDU {pduSize} 字节，TPDU 上限 {tpduSize} 字节。");
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (session.IsCancellationRequested &&
                                       (ex is OperationCanceledException || ex is InvalidOperationException ||
                                        ex is IOException || ex is SocketException))
            {
            }
            catch (EndOfStreamException)
            {
                if (cotpConfirmed && !handshakeComplete)
                {
                    _log?.Invoke($"COTP 已确认，S7 参数协商完成前对端 {peer} 关闭了 TCP 连接。");
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"{phase}中断，对端 {peer}。{DescribeException(ex)}");
            }
            finally
            {
                Interlocked.Decrement(ref _activeTcpConnectionCount);
                if (session.IsCancellationRequested && !stopping.IsCancellationRequested)
                {
                    var timeoutMs = handshakeComplete ? _frameTimeoutMs : _handshakeTimeoutMs;
                    _log?.Invoke($"{phase}超时，会话时限 {timeoutMs} ms，已关闭对端 {peer} 的连接。");
                }
            }
        }
    }

    private void EnableTcpKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                uint[] settings = { 1, 30000, 5000 };
                var buffer = new byte[12];
                Buffer.BlockCopy(settings, 0, buffer, 0, buffer.Length);
                socket.IOControl(IOControlCode.KeepAliveValues, buffer, null);
            }
        }
        catch (Exception ex) when (ex is SocketException || ex is NotSupportedException)
        {
            if (Interlocked.Exchange(ref _keepAliveWarningLogged, 1) == 0)
            {
                _log?.Invoke($"S7 TCP KeepAlive 设置失败。{DescribeException(ex)}");
            }
        }
    }

    private static string DescribeException(Exception exception)
    {
        var cause = exception.GetBaseException();
        return cause is SocketException socket
            ? $"{socket.SocketErrorCode} ({socket.NativeErrorCode}) {socket.Message}"
            : $"{cause.GetType().Name} {cause.Message}";
    }

    private static async Task<byte[]> ReadTpktAsync(NetworkStream stream, int maxTpduLength,
        CancellationTokenSource session, int timeoutOnFirstByte)
    {
        var header = new byte[TpktHeaderLength];
        var token = session.Token;
        int firstRead = await stream.ReadAsync(header, 0, header.Length, token).ConfigureAwait(false);
        if (firstRead == 0)
        {
            throw new EndOfStreamException();
        }

        if (timeoutOnFirstByte > 0)
        {
            session.CancelAfter(timeoutOnFirstByte);
        }

        await ReadFrameBytesAsync(stream, header, firstRead, token).ConfigureAwait(false);
        int length = ReadUInt16BigEndian(header, 2) - TpktHeaderLength;
        if (header[0] != 0x03 || header[1] != 0x00 || length < CotpDataHeaderLength || length > maxTpduLength)
        {
            throw new InvalidDataException(
                $"TPKT 头无效，版本 0x{header[0]:X2}，保留字节 0x{header[1]:X2}，TPDU 长度 {length}，上限 {maxTpduLength}。");
        }

        var body = new byte[length];
        await ReadFrameBytesAsync(stream, body, 0, token).ConfigureAwait(false);
        return body;
    }

    private static async Task ReadFrameBytesAsync(NetworkStream stream, byte[] buffer, int offset,
        CancellationToken stopping)
    {
        while (offset < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer, offset, buffer.Length - offset, stopping)
                .ConfigureAwait(false);
            if (count == 0)
            {
                throw new IOException("连接在 TPKT 帧接收完成前关闭。");
            }

            offset += count;
        }
    }

    private static Task WriteTpktAsync(NetworkStream stream, byte[] tpdu, CancellationToken stopping)
    {
        var frame = new byte[TpktHeaderLength + tpdu.Length];
        frame[0] = 0x03;
        WriteUInt16BigEndian(frame, 2, frame.Length);
        Buffer.BlockCopy(tpdu, 0, frame, TpktHeaderLength, tpdu.Length);
        return stream.WriteAsync(frame, 0, frame.Length, stopping);
    }

    private static async Task WriteS7Async(NetworkStream stream, byte[] pdu, int tpduSize,
        CancellationToken stopping)
    {
        int maxPayload = tpduSize - CotpDataHeaderLength;
        for (int offset = 0; offset < pdu.Length; offset += maxPayload)
        {
            int length = Math.Min(maxPayload, pdu.Length - offset);
            var tpdu = new byte[CotpDataHeaderLength + length];
            tpdu[0] = 2;
            tpdu[1] = 0xF0;
            tpdu[2] = offset + length == pdu.Length ? (byte)0x80 : (byte)0;
            Buffer.BlockCopy(pdu, offset, tpdu, CotpDataHeaderLength, length);
            await WriteTpktAsync(stream, tpdu, stopping).ConfigureAwait(false);
        }
    }

    private static bool TryConfirmCotp(byte[] request, out byte[] confirmation, out int tpduSize)
    {
        confirmation = EmptyBytes;
        tpduSize = MaxTpduSize;
        if (request.Length < 7 || request[0] + 1 != request.Length || request[1] != 0xE0 ||
            ReadUInt16BigEndian(request, 2) != 0 || request[6] != 0)
        {
            return false;
        }

        // RFC 1006：缺省 TPDU 上限为 65531，C0 存在时协商较小值。
        var reply = new List<byte>(request.Length) { 0, 0xD0, request[4], request[5], 0, 1, 0 };
        for (int offset = 7; offset < request.Length;)
        {
            if (request.Length - offset < 2 || request[offset + 1] > request.Length - offset - 2)
            {
                return false;
            }

            byte parameter = request[offset];
            int length = request[offset + 1];
            if (parameter == 0xC0)
            {
                if (length != 1 || request[offset + 2] < 7 || request[offset + 2] > 13)
                {
                    return false;
                }

                int sizeCode = Math.Min((int)request[offset + 2], 10);
                tpduSize = 1 << sizeCode;
                reply.AddRange(new byte[] { 0xC0, 1, (byte)sizeCode });
            }
            else if (parameter == 0xC1 || parameter == 0xC2)
            {
                for (int index = offset; index < offset + length + 2; index++)
                {
                    reply.Add(request[index]);
                }
            }

            offset += length + 2;
        }

        reply[0] = (byte)(reply.Count - 1);
        confirmation = reply.ToArray();
        return true;
    }

    private byte[] ProcessS7Request(byte[] request, int requestLength, ref int pduSize)
    {
        if (requestLength < S7HeaderLength || request[0] != 0x32)
        {
            return EmptyBytes;
        }

        int reference = ReadUInt16BigEndian(request, 4);
        int parameterLength = ReadUInt16BigEndian(request, 6);
        int dataLength = ReadUInt16BigEndian(request, 8);
        if (requestLength > (pduSize == 0 ? MaxPduSize : pduSize))
        {
            return BuildS7Message(AckData, reference, EmptyBytes, EmptyBytes, PduSizeError);
        }

        int dataOffset = S7HeaderLength + parameterLength;
        if (parameterLength == 0 || dataOffset + dataLength != requestLength)
        {
            return BuildS7Message(AckData, reference, EmptyBytes, EmptyBytes, InvalidParameter);
        }

        byte function = request[S7HeaderLength];
        if (request[1] == Job && function == SetupCommunication)
        {
            if (parameterLength != 8 || dataLength != 0 ||
                ReadUInt16BigEndian(request, S7HeaderLength + 2) == 0 ||
                ReadUInt16BigEndian(request, S7HeaderLength + 4) == 0)
            {
                return BuildS7Message(AckData, reference, EmptyBytes, EmptyBytes, InvalidParameter);
            }

            int requestedPdu = ReadUInt16BigEndian(request, S7HeaderLength + 6);
            if (requestedPdu < MinPduSize)
            {
                return BuildS7Message(AckData, reference, EmptyBytes, EmptyBytes, InvalidParameter);
            }

            // 每条连接按请求顺序处理一个作业，PDU 长度按该连接协商。
            pduSize = Math.Min(requestedPdu, MaxPduSize);
            byte[] reply = { SetupCommunication, 0, 0, 1, 0, 1, 0, 0 };
            WriteUInt16BigEndian(reply, 6, pduSize);
            return BuildS7Message(AckData, reference, reply, EmptyBytes);
        }

        if (pduSize == 0)
        {
            return BuildS7Message(AckData, reference, EmptyBytes, EmptyBytes, FunctionNotSupported);
        }

        var parameter = new ArraySegment<byte>(request, S7HeaderLength, parameterLength);
        var data = new ArraySegment<byte>(request, dataOffset, dataLength);
        if (request[1] == UserData)
        {
            return ReadSystemStatusList(reference, parameter, data);
        }

        if (request[1] != Job || (function != ReadVar && function != WriteVar))
        {
            return BuildS7Message(AckData, reference, EmptyBytes, EmptyBytes, FunctionNotSupported);
        }

        if (!TryReadVariableItems(parameter, out var items) || (function == ReadVar && dataLength != 0))
        {
            return BuildS7Message(AckData, reference, EmptyBytes, EmptyBytes, InvalidParameter);
        }

        lock (_networkMemoryLock)
        {
            return function == ReadVar
                ? ReadVariables(reference, items, pduSize)
                : WriteVariables(reference, items, data);
        }
    }

    private static bool TryReadVariableItems(ArraySegment<byte> parameter, out VariableItem[] items)
    {
        items = Array.Empty<VariableItem>();
        var buffer = parameter.Array;
        int start = parameter.Offset;
        int end = start + parameter.Count;
        if (parameter.Count < 2 || buffer[start + 1] == 0)
        {
            return false;
        }

        items = new VariableItem[buffer[start + 1]];
        int offset = start + 2;
        for (int index = 0; index < items.Length; index++)
        {
            if (end - offset < 2 || buffer[offset + 1] > end - offset - 2)
            {
                return false;
            }

            int itemLength = buffer[offset + 1] + 2;
            if (itemLength == 12 && buffer[offset] == 0x12 && buffer[offset + 2] == 0x10)
            {
                int bitAddress = (buffer[offset + 9] << 16) | (buffer[offset + 10] << 8) | buffer[offset + 11];
                items[index] = new VariableItem(buffer[offset + 3], ReadUInt16BigEndian(buffer, offset + 4),
                    ReadUInt16BigEndian(buffer, offset + 6), buffer[offset + 8], bitAddress);
            }

            offset += itemLength;
        }

        return offset == end;
    }

    private byte[] ReadVariables(int reference, VariableItem[] items, int pduSize)
    {
        int dataCapacity = pduSize - S7AckHeaderLength - 2;
        var response = new List<byte>(dataCapacity);

        for (int index = 0; index < items.Length; index++)
        {
            var item = items[index];
            byte status = ResolveMemory(item, out var memory, out var type);
            int payloadLength = status == Success ? type.PayloadLength(item.Count) : 0;
            bool pad = index < items.Length - 1 && (payloadLength & 1) != 0;
            if (response.Count + 4 * (items.Length - index) + payloadLength + (pad ? 1 : 0) > dataCapacity)
            {
                return BuildS7Message(AckData, reference, EmptyBytes, EmptyBytes, PduSizeError);
            }

            if (status != Success)
            {
                response.AddRange(new byte[] { status, 0, 0, 0 });
                continue;
            }

            int bitOffset = item.BitAddress & 7;
            int memoryLength = type.IsBit ? (bitOffset + item.Count + 7) / 8 : payloadLength;
            var result = Read(memory, (ushort)memoryLength);
            if (!result.IsSuccess || result.Content?.Length != memoryLength)
            {
                response.AddRange(new byte[] { AddressOutOfRange, 0, 0, 0 });
                continue;
            }

            var payload = result.Content;
            if (type.IsBit)
            {
                payload = new byte[payloadLength];
                for (int bit = 0; bit < item.Count; bit++)
                {
                    int sourceBit = bitOffset + bit;
                    if ((result.Content[sourceBit / 8] & (1 << (sourceBit & 7))) != 0)
                    {
                        payload[bit / 8] |= (byte)(1 << (bit & 7));
                    }
                }
            }

            int lengthField = type.IsBit
                ? item.Count
                : UsesBitLength(type.TransportSize) ? payloadLength * 8 : payloadLength;
            response.Add(Success);
            response.Add(type.TransportSize);
            response.Add((byte)(lengthField >> 8));
            response.Add((byte)(lengthField & 0xFF));
            response.AddRange(payload);
            if (pad)
            {
                response.Add(0);
            }
        }

        return BuildS7Message(AckData, reference, new byte[] { ReadVar, (byte)items.Length }, response.ToArray());
    }

    private byte[] WriteVariables(int reference, VariableItem[] items, ArraySegment<byte> data)
    {
        if (!TryReadWriteData(data, items.Length, out var dataItems))
        {
            return BuildS7Message(AckData, reference, EmptyBytes, EmptyBytes, InvalidParameter);
        }

        var status = new byte[items.Length];
        for (int index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var value = dataItems[index];
            byte result = ResolveMemory(item, out var memory, out var type);
            if (result == Success)
            {
                bool matchesType = (value.TransportSize == Bit) == type.IsBit;
                bool matchesLength = value.Length == type.PayloadLength(item.Count) &&
                                     (type.IsBit
                                         ? value.LengthField == item.Count || value.LengthField == value.Length * 8
                                         : !UsesBitLength(value.TransportSize) || value.LengthField == value.Length * 8);
                result = matchesType && matchesLength
                    ? WriteVariable(item, memory, type, data.Array, value)
                    : DataTypeMismatch;
            }

            status[index] = result;
        }

        // Write Var 应答的数据区按请求顺序放置每项的一个返回码。
        return BuildS7Message(AckData, reference, new byte[] { WriteVar, (byte)items.Length }, status);
    }

    private static bool TryReadWriteData(ArraySegment<byte> data, int count, out WriteDataItem[] items)
    {
        items = new WriteDataItem[count];
        var buffer = data.Array;
        int offset = data.Offset;
        int end = offset + data.Count;
        for (int index = 0; index < count; index++)
        {
            if (end - offset < 4)
            {
                return false;
            }

            // 首字节在写请求中不参与寻址，按后续传输类型和长度字段解析。
            byte transportSize = buffer[offset + 1];
            int lengthField = ReadUInt16BigEndian(buffer, offset + 2);
            int length;
            if (UsesBitLength(transportSize))
            {
                length = (lengthField + 7) / 8;
            }
            else if (transportSize == DoubleInteger || transportSize == Real || transportSize == OctetString)
            {
                length = lengthField;
            }
            else
            {
                return false;
            }

            if (length > end - offset - 4)
            {
                return false;
            }

            items[index] = new WriteDataItem(transportSize, lengthField, offset + 4, length);
            offset += 4 + length;
            if ((length & 1) != 0 && index < count - 1)
            {
                if (offset == end)
                {
                    return false;
                }

                offset++;
            }
        }

        return offset == end ||
               (items[count - 1].Length & 1) != 0 && offset + 1 == end && buffer[offset] == 0;
    }

    private byte WriteVariable(VariableItem item, string memory, VariableType type, byte[] packet, WriteDataItem value)
    {
        if (!type.IsBit)
        {
            var payload = new byte[value.Length];
            Buffer.BlockCopy(packet, value.Offset, payload, 0, value.Length);
            return Write(memory, payload).IsSuccess ? Success : AddressOutOfRange;
        }

        string prefix = MemoryPrefix(item);
        for (int bit = 0; bit < item.Count; bit++)
        {
            int targetBit = item.BitAddress + bit;
            string address = string.Format(CultureInfo.InvariantCulture, "{0}{1}.{2}", prefix,
                targetBit / 8, targetBit & 7);
            bool bitValue = (packet[value.Offset + bit / 8] & (1 << (bit & 7))) != 0;
            if (!Write(address, bitValue).IsSuccess)
            {
                return AddressOutOfRange;
            }
        }

        return Success;
    }

    private byte ResolveMemory(VariableItem item, out string memory, out VariableType type)
    {
        memory = string.Empty;
        switch (item.Type)
        {
            case 0x01:
                type = new VariableType(Bit, 0);
                break;
            case 0x02:
                type = new VariableType(ByteWordDword, 1);
                break;
            case 0x03:
                type = new VariableType(OctetString, 1);
                break;
            case 0x04:
            case 0x09:
            case 0x0C:
                type = new VariableType(ByteWordDword, 2);
                break;
            case 0x05:
                type = new VariableType(Integer, 2);
                break;
            case 0x06:
            case 0x0A:
            case 0x0B:
                type = new VariableType(ByteWordDword, 4);
                break;
            case 0x07:
                type = new VariableType(DoubleInteger, 4);
                break;
            case 0x08:
                type = new VariableType(Real, 4);
                break;
            case 0x0F:
                type = new VariableType(OctetString, 8);
                break;
            default:
                type = default(VariableType);
                return UnsupportedType;
        }

        if (item.Count == 0 || !type.IsBit && (item.BitAddress & 7) != 0)
        {
            return AddressOutOfRange;
        }

        var prefix = MemoryPrefix(item);
        if (prefix.Length == 0)
        {
            return ObjectNotFound;
        }

        int byteOffset = item.BitAddress / 8;
        if (item.Area == 0x84)
        {
            if (!_dbLengths.TryGetValue(item.DbNumber, out int length))
            {
                return ObjectNotFound;
            }

            int memoryLength = type.IsBit ? ((item.BitAddress & 7) + item.Count + 7) / 8 : type.PayloadLength(item.Count);
            if (byteOffset + memoryLength > length)
            {
                return AddressOutOfRange;
            }
        }

        memory = prefix + byteOffset.ToString(CultureInfo.InvariantCulture);
        return Success;
    }

    private static string MemoryPrefix(VariableItem item)
    {
        switch (item.Area)
        {
            case 0x81: return "I";
            case 0x82: return "Q";
            case 0x83: return "M";
            case 0x84: return "DB" + item.DbNumber.ToString(CultureInfo.InvariantCulture) + ".";
            default: return string.Empty;
        }
    }

    private byte[] ReadSystemStatusList(int reference, ArraySegment<byte> parameter, ArraySegment<byte> data)
    {
        // UserData，CPU Functions，Read SZL。
        var parameterBuffer = parameter.Array;
        var dataBuffer = data.Array;
        int start = parameter.Offset;
        int dataOffset = data.Offset;
        if (parameter.Count != 8 || data.Count != 8 ||
            parameterBuffer[start] != 0 || parameterBuffer[start + 1] != 1 ||
            parameterBuffer[start + 2] != 0x12 || parameterBuffer[start + 3] != 4 ||
            parameterBuffer[start + 4] != 0x11 || parameterBuffer[start + 5] != 0x44 ||
            parameterBuffer[start + 6] != 1 || dataBuffer[dataOffset + 1] != OctetString ||
            ReadUInt16BigEndian(dataBuffer, dataOffset + 2) != 4)
        {
            return BuildS7Message(AckData, reference, EmptyBytes, EmptyBytes, FunctionNotSupported);
        }

        int id = ReadUInt16BigEndian(dataBuffer, dataOffset + 4);
        int index = ReadUInt16BigEndian(dataBuffer, dataOffset + 6);
        byte[] responseParameter = { 0, 1, 0x12, 8, 0x12, 0x84, 1, parameterBuffer[start + 7], 0, 0, 0, 0 };

        if ((id == 0x0011 && index == 0) || (id == 0x0111 && index == 1))
        {
            return BuildS7Message(UserData, reference, responseParameter,
                BuildSzlData(id, index, 28, new SzlRecord(1, _orderNumber.PadRight(20))));
        }

        // S7-1200/1500 的 CPU 识别读取 001C/0001，记录内包含订货号。
        if (id == 0x001C && index == 1)
        {
            return BuildS7Message(UserData, reference, responseParameter,
                BuildSzlData(id, index, 28, new SzlRecord(1, _orderNumber.PadRight(20))));
        }

        if (id == 0x001C && index == 0)
        {
            return BuildS7Message(UserData, reference, responseParameter,
                BuildSzlData(id, index, 34, new SzlRecord(1, "VDA5050 PLC Simulator"),
                    new SzlRecord(2, _cpuName), new SzlRecord(7, _cpuName)));
        }

        if (id == 0x011C && (index == 1 || index == 2 || index == 7))
        {
            string value = index == 1 ? "VDA5050 PLC Simulator" : _cpuName;
            return BuildS7Message(UserData, reference, responseParameter,
                BuildSzlData(id, index, 34, new SzlRecord(index, value)));
        }

        WriteUInt16BigEndian(responseParameter, 10, 0xD401);
        return BuildS7Message(UserData, reference, responseParameter, new byte[] { ObjectNotFound, 0, 0, 0 });
    }

    private static byte[] BuildSzlData(int id, int index, int recordLength, params SzlRecord[] records)
    {
        var data = new byte[12 + recordLength * records.Length];
        data[0] = Success;
        data[1] = OctetString;
        WriteUInt16BigEndian(data, 2, data.Length - 4);
        WriteUInt16BigEndian(data, 4, id);
        WriteUInt16BigEndian(data, 6, index);
        WriteUInt16BigEndian(data, 8, recordLength);
        WriteUInt16BigEndian(data, 10, records.Length);
        int offset = 12;
        foreach (var record in records)
        {
            WriteUInt16BigEndian(data, offset, record.Index);
            int textLength = Math.Min(record.Value.Length, recordLength - 2);
            Encoding.ASCII.GetBytes(record.Value, 0, textLength, data, offset + 2);
            offset += recordLength;
        }

        return data;
    }

    private static byte[] BuildS7Message(byte type, int reference, byte[] parameter, byte[] data, ushort error = 0)
    {
        int headerLength = type == AckData ? S7AckHeaderLength : S7HeaderLength;
        var message = new byte[headerLength + parameter.Length + data.Length];
        message[0] = 0x32;
        message[1] = type;
        WriteUInt16BigEndian(message, 4, reference);
        WriteUInt16BigEndian(message, 6, parameter.Length);
        WriteUInt16BigEndian(message, 8, data.Length);
        if (type == AckData)
        {
            WriteUInt16BigEndian(message, 10, error);
        }

        Buffer.BlockCopy(parameter, 0, message, headerLength, parameter.Length);
        Buffer.BlockCopy(data, 0, message, headerLength + parameter.Length, data.Length);
        return message;
    }

    private static bool UsesBitLength(byte transportSize) =>
        transportSize == Bit || transportSize == ByteWordDword || transportSize == Integer;

    private static int ReadUInt16BigEndian(byte[] buffer, int offset)
    {
        return (buffer[offset] << 8) | buffer[offset + 1];
    }

    private static void WriteUInt16BigEndian(byte[] buffer, int offset, int value)
    {
        ushort number = checked((ushort)value);
        buffer[offset] = (byte)(number >> 8);
        buffer[offset + 1] = (byte)(number & 0xFF);
    }

    private readonly struct VariableItem
    {
        public VariableItem(byte type, int count, int dbNumber, byte area, int bitAddress)
        {
            Type = type;
            Count = count;
            DbNumber = dbNumber;
            Area = area;
            BitAddress = bitAddress;
        }

        public byte Type { get; }
        public int Count { get; }
        public int DbNumber { get; }
        public byte Area { get; }
        public int BitAddress { get; }
    }

    private readonly struct WriteDataItem
    {
        public WriteDataItem(byte transportSize, int lengthField, int offset, int length)
        {
            TransportSize = transportSize;
            LengthField = lengthField;
            Offset = offset;
            Length = length;
        }

        public byte TransportSize { get; }
        public int LengthField { get; }
        public int Offset { get; }
        public int Length { get; }
    }

    private readonly struct VariableType
    {
        public VariableType(byte transportSize, int bytesPerElement)
        {
            TransportSize = transportSize;
            BytesPerElement = bytesPerElement;
        }

        public byte TransportSize { get; }
        private int BytesPerElement { get; }
        public bool IsBit => TransportSize == Bit;

        public int PayloadLength(int count) => IsBit ? (count + 7) / 8 : count * BytesPerElement;
    }

    private readonly struct SzlRecord
    {
        public SzlRecord(int index, string value)
        {
            Index = index;
            Value = value;
        }

        public int Index { get; }
        public string Value { get; }
    }
}
