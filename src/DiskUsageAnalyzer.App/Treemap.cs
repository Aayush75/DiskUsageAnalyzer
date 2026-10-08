using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskUsageAnalyzer.Core;

namespace DiskUsageAnalyzer.App;

public sealed class Treemap : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IEnumerable), typeof(Treemap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));
    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(nameof(SelectedItem), typeof(FileEntry), typeof(Treemap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public IEnumerable? Items { get => (IEnumerable?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public FileEntry? SelectedItem { get => (FileEntry?)GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    private readonly List<(Rect Area, FileEntry File)> _areas = [];
    private static readonly Color[] Palette = [Color.FromRgb(57, 100, 174), Color.FromRgb(47, 132, 134), Color.FromRgb(104, 86, 162), Color.FromRgb(48, 111, 150), Color.FromRgb(137, 106, 58), Color.FromRgb(116, 75, 113)];
    public Treemap() { Cursor = Cursors.Hand; MouseMove += OnHover; MouseLeftButtonDown += OnClick; }
    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var map = (Treemap)d;
        if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= map.CollectionChanged;
        if (e.NewValue is INotifyCollectionChanged current) current.CollectionChanged += map.CollectionChanged;
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); _areas.Clear();
        var files = Items?.Cast<FileEntry>().Where(f => f.Bytes > 0).Take(120).OrderByDescending(f => f.Bytes).ToList() ?? [];
        if (ActualWidth < 1 || ActualHeight < 1) return;
        if (files.Count == 0)
        {
            var text = Label("Scan storage to see the big picture.", 13, Brushes.Gray); dc.DrawText(text, new Point(12, 28)); return;
        }
        Layout(files, new Rect(0, 0, ActualWidth, ActualHeight));
        int index = 0;
        foreach (var (area, file) in _areas)
        {
            var inner = new Rect(area.X + 2, area.Y + 2, Math.Max(0, area.Width - 4), Math.Max(0, area.Height - 4));
            if (inner.Width < 2 || inner.Height < 2) continue;
            var brush = new SolidColorBrush(Palette[index++ % Palette.Length]);
            dc.DrawRoundedRectangle(brush, new Pen(SelectedItem?.Id == file.Id ? Brushes.White : brush, SelectedItem?.Id == file.Id ? 2 : 0), inner, 5, 5);
            if (inner.Width > 70 && inner.Height > 42)
            {
                var name = Label(file.Name, 12, Brushes.White); name.MaxTextWidth = Math.Max(1, inner.Width - 16); name.MaxTextHeight = 18; name.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(name, new Point(inner.X + 8, inner.Y + 8));
                dc.DrawText(Label(file.Size, 10, Brushes.White), new Point(inner.X + 8, inner.Y + 27));
            }
        }
    }
    private void Layout(List<FileEntry> files, Rect area)
    {
        if (files.Count == 1) { _areas.Add((area, files[0])); return; }
        double total = files.Sum(f => (double)f.Bytes), sum = 0; int split = 0;
        while (split < files.Count - 1 && sum < total / 2) sum += files[split++].Bytes;
        split = Math.Max(1, split); double ratio = sum / total;
        if (area.Width >= area.Height)
        {
            double width = area.Width * ratio; Layout(files.Take(split).ToList(), new(area.X, area.Y, width, area.Height)); Layout(files.Skip(split).ToList(), new(area.X + width, area.Y, area.Width - width, area.Height));
        }
        else
        {
            double height = area.Height * ratio; Layout(files.Take(split).ToList(), new(area.X, area.Y, area.Width, height)); Layout(files.Skip(split).ToList(), new(area.X, area.Y + height, area.Width, area.Height - height));
        }
    }
    private FormattedText Label(string s, double size, Brush brush) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private void OnHover(object sender, MouseEventArgs e) { var hit = _areas.FirstOrDefault(a => a.Area.Contains(e.GetPosition(this))); ToolTip = hit.File is null ? null : $"{hit.File.Path}\n{hit.File.Size}"; }
    private void OnClick(object sender, MouseButtonEventArgs e) { var hit = _areas.FirstOrDefault(a => a.Area.Contains(e.GetPosition(this))); if (hit.File is not null) { SelectedItem = hit.File; if (DataContext is MainViewModel vm) { vm.Section = 1; if (e.ClickCount == 2 && vm.OpenFolderCommand.CanExecute(null)) vm.OpenFolderCommand.Execute(null); } } }
}
