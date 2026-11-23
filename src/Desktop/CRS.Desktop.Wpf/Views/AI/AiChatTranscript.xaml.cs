using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace CRS.DesktopClient.Views.AI;

/// <summary>面板和历史页面共用只读问答展示，当前对话有新回复时滚动到最新一条。</summary>
public partial class AiChatTranscript : UserControl
{
    public static readonly DependencyProperty EntriesProperty = DependencyProperty.Register(nameof(Entries),
        typeof(IEnumerable), typeof(AiChatTranscript), new PropertyMetadata(null, OnEntriesChanged));

    public AiChatTranscript() => InitializeComponent();

    public IEnumerable? Entries
    {
        get => (IEnumerable?)GetValue(EntriesProperty);
        set => SetValue(EntriesProperty, value);
    }

    private static void OnEntriesChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (AiChatTranscript)sender;
        if (args.OldValue is INotifyCollectionChanged old)
            CollectionChangedEventManager.RemoveHandler(old, view.OnCollectionChanged);
        if (args.NewValue is INotifyCollectionChanged current)
            CollectionChangedEventManager.AddHandler(current, view.OnCollectionChanged);
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems is { Count: > 0 })
        {
            var item = args.NewItems[args.NewItems.Count - 1];
            // 等待 ItemsSource 和容器更新后定位，关闭或锁定后不再滚动。
            Dispatcher.BeginInvoke(() =>
            {
                if (IsLoaded && MessageList.Items.Contains(item)) MessageList.ScrollIntoView(item);
            });
        }
    }
}
