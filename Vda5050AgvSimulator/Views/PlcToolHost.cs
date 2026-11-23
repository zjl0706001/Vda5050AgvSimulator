using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

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
            var close = new Button();
            close.SetResourceReference(StyleProperty, "WorkspaceTabCloseStyle");
            close.SetBinding(AutomationProperties.NameProperty,
                new Binding(nameof(page.Title)) { Source = page, StringFormat = "关闭 {0}" });
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

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (ReferenceEquals(e.OriginalSource, this) && SelectedItem is TabItem selected)
        {
            // New tabs acquire their header layout after selection has changed.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (selected.IsSelected && selected.IsVisible)
                    selected.BringIntoView();
            }));
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F4 && Keyboard.Modifiers == ModifierKeys.Control &&
            SelectedContent is PlcToolPage page)
        {
            page.Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
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
