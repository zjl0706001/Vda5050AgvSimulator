using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Vda5050AgvSimulator.Views;

public class PlcToolPage : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PlcToolPage), new PropertyMetadata(string.Empty));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    internal PlcToolHost? Workspace { get; set; }
    public event CancelEventHandler? Closing;
    public event EventHandler? Closed;

    public void Close() => Workspace?.RemovePage(this, force: false);

    internal bool CanClose()
    {
        var args = new CancelEventArgs();
        Closing?.Invoke(this, args);
        return !args.Cancel;
    }

    internal void NotifyClosed() => Closed?.Invoke(this, EventArgs.Empty);
}

public class PlcToolHost : TabControl
{
    public IEnumerable<PlcToolPage> Pages =>
        Items.OfType<TabItem>().Select(tab => tab.Content).OfType<PlcToolPage>();

    public void Open(PlcToolPage page)
    {
        var tab = Items.OfType<TabItem>().FirstOrDefault(item => ReferenceEquals(item.Content, page));
        if (tab == null)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            var title = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 200,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var titleBinding = new Binding(nameof(page.Title)) { Source = page };
            title.SetBinding(TextBlock.TextProperty, titleBinding);
            title.SetBinding(ToolTipProperty, titleBinding);
            var close = new Button
            {
                Content = "×",
                Padding = new Thickness(4, 0, 4, 0),
                MinWidth = 20,
                MinHeight = 20,
                Margin = new Thickness(6, 0, 0, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ToolTip = "关闭此页"
            };
            AutomationProperties.SetName(close, "关闭此页");
            close.Click += (_, args) =>
            {
                args.Handled = true;
                page.Close();
            };
            header.Children.Add(title);
            header.Children.Add(close);
            tab = new TabItem { Header = header, Content = page };
            tab.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(page.Title)) { Source = page });
            page.Workspace = this;
            Items.Add(tab);
        }
        SelectedItem = tab;
    }

    internal void RemovePage(PlcToolPage page, bool force)
    {
        var tab = Items.OfType<TabItem>().FirstOrDefault(item => ReferenceEquals(item.Content, page));
        if (tab == null || (!force && !page.CanClose()))
            return;

        int index = Items.IndexOf(tab);
        if (tab.IsSelected && index > 0)
            SelectedIndex = index - 1;

        Items.Remove(tab);
        tab.Content = null;
        page.Workspace = null;
        page.NotifyClosed();
    }

    public void CloseAll()
    {
        foreach (var page in Pages.ToArray())
            RemovePage(page, force: true);
    }
}
