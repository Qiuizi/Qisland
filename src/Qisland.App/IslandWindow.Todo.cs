using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Qisland.App;

/// <summary>待办页：岛上直接输入添加、点圆圈勾掉、右键删除，已完成的可以点回来。</summary>
public sealed partial class IslandWindow
{
    public TodoFile? Todo { get; set; }
    private TextBox? _todoBox;
    private TextBlock? _todoHint;
    private bool _editing;

    private void BuildTodo(StackPanel stack)
    {
        var items = Todo?.Items ?? [];
        var open = items.Where(i => !i.Done).ToList();
        var done = items.Where(i => i.Done).Reverse().Take(3).ToList();

        var head = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Glyph("\uE73A", 14, Theme.Red));
        var tt = Txt(13, FontWeights.SemiBold, Theme.Primary); tt.Text = "待办"; tt.Margin = new Thickness(7, 0, 0, 0);
        title.Children.Add(tt);
        var n = Txt(12, FontWeights.Normal, Theme.Tertiary); n.Text = open.Count > 0 ? $"  {open.Count} 件" : "  全部完成"; title.Children.Add(n);
        head.Children.Add(title);
        if (items.Any(i => i.Done))
        {
            var clear = Txt(12, FontWeights.Medium, Theme.Secondary); clear.Text = "清除已完成"; clear.Cursor = Cursors.Hand;
            clear.HorizontalAlignment = HorizontalAlignment.Right; clear.VerticalAlignment = VerticalAlignment.Center;
            clear.MouseLeftButtonUp += (_, e) => { Todo?.ClearDone(); e.Handled = true; };
            head.Children.Add(clear);
        }
        stack.Children.Add(head);
        stack.Children.Add(TodoInput());

        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var it in open.Take(7)) list.Children.Add(TodoRow(it));
        if (open.Count > 7)
        {
            var more = Txt(11.5, FontWeights.Medium, Theme.Tertiary); more.Text = $"还有 {open.Count - 7} 件"; more.Margin = new Thickness(36, 2, 0, 2);
            list.Children.Add(more);
        }
        if (open.Count == 0 && done.Count == 0)
        {
            var empty = Txt(12.5, FontWeights.Normal, Theme.Tertiary); empty.Text = "还没有待办，写下第一件事吧";
            empty.HorizontalAlignment = HorizontalAlignment.Center; empty.Margin = new Thickness(0, 14, 0, 10);
            list.Children.Add(empty);
        }
        if (done.Count > 0)
        {
            var h = Txt(11, FontWeights.SemiBold, Theme.Tertiary); h.Text = "已完成"; h.Margin = new Thickness(6, 8, 0, 2);
            list.Children.Add(h);
            foreach (var it in done) list.Children.Add(TodoRow(it));
        }
        stack.Children.Add(list);
    }

    private FrameworkElement TodoInput()
    {
        if (_todoBox == null)
        {
            _todoBox = new TextBox
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontFamily = Theme.Text, FontSize = 13,
                VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0), FocusVisualStyle = null,
                SelectionBrush = Theme.Blue, MaxLength = 200,
            };
            _todoBox.PreviewMouseLeftButtonDown += (_, _) => BeginEditing();
            _todoBox.TextChanged += (_, _) => { if (_todoHint != null) _todoHint.Visibility = _todoBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            _todoBox.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && !e.IsRepeat)
                {
                    var text = _todoBox.Text;
                    _todoBox.Clear();
                    if (text.Trim().Length > 0) Todo?.Add(text);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape) { EndEditing(); e.Handled = true; }
            };
            _todoBox.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() => { if (_todoBox != null && !_todoBox.IsKeyboardFocusWithin && !IsActive) EndEditing(); });
            _todoHint = new TextBlock { Text = "添加待办，回车保存", FontFamily = Theme.Text, FontSize = 13, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        }
        _todoBox.Foreground = Theme.Primary;
        _todoBox.CaretBrush = Theme.Primary;
        _todoHint!.Foreground = Theme.Tertiary;
        (_todoBox.Parent as Panel)?.Children.Remove(_todoBox);
        (_todoHint.Parent as Panel)?.Children.Remove(_todoHint);

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var plus = Glyph("\uE710", 13, Theme.Secondary); plus.Margin = new Thickness(0, 0, 10, 0);
        g.Children.Add(plus);
        var cell = new Grid();
        cell.Children.Add(_todoHint);
        cell.Children.Add(_todoBox);
        Grid.SetColumn(cell, 1);
        g.Children.Add(cell);
        var border = new Border { Height = 38, CornerRadius = new CornerRadius(14), Background = Theme.Fill, Padding = new Thickness(13, 0, 12, 0), Child = g, Cursor = Cursors.IBeam };
        border.MouseLeftButtonDown += (_, e) => { BeginEditing(); e.Handled = true; };
        if (_editing) Dispatcher.BeginInvoke(() => _todoBox?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        return border;
    }

    /// <summary>平时窗口不抢焦点（NOACTIVATE）；要打字时临时允许激活，打完立刻还回去。</summary>
    private void BeginEditing()
    {
        if (_editing) { _todoBox?.Focus(); return; }
        _editing = true;
        Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE, false);
        Launcher.UnlockForeground();
        Activate();
        _todoBox?.Focus();
        Keyboard.Focus(_todoBox);
    }

    private void EndEditing()
    {
        if (!_editing) return;
        _editing = false;
        Keyboard.ClearFocus();
        Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE, true);
    }

    private FrameworkElement TodoRow(TodoItem it)
    {
        var g = new Grid { Height = 34, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(6, 0, 6, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var circle = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var ring = new Ellipse { StrokeThickness = 1.5, Stroke = it.Done ? Theme.Blue : Theme.Tertiary, Fill = it.Done ? Theme.Blue : Brushes.Transparent };
        var check = Glyph("\uE73E", 10, Brushes.White);
        check.HorizontalAlignment = HorizontalAlignment.Center;
        check.FontWeight = FontWeights.Bold;
        check.Opacity = it.Done ? 1 : 0;
        circle.Children.Add(ring);
        circle.Children.Add(check);
        g.Children.Add(circle);
        var tx = Txt(13, FontWeights.Normal, it.Done ? Theme.Tertiary : Theme.Primary);
        tx.Text = it.Text;
        tx.VerticalAlignment = VerticalAlignment.Center;
        tx.ToolTip = it.Text;
        if (it.Done) tx.TextDecorations = TextDecorations.Strikethrough;
        Grid.SetColumn(tx, 1);
        g.Children.Add(tx);
        AddHover(g);

        g.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (it.Done) { Todo?.SetDone(it, false); return; }
            // 先播完勾选动画再落盘，手感像 iOS 提醒事项
            ring.Stroke = Theme.Blue;
            ring.Fill = Theme.Blue;
            check.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            var st = new ScaleTransform(1, 1, 9, 9);
            circle.RenderTransform = st;
            var pop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(260) };
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.25, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110))));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), new BackEase { EasingMode = EasingMode.EaseOut }));
            st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            tx.Foreground = Theme.Tertiary;
            tx.TextDecorations = TextDecorations.Strikethrough;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
            t.Tick += (_, _) => { t.Stop(); Todo?.SetDone(it, true); };
            t.Start();
        };
        g.MouseRightButtonUp += (_, e) => { e.Handled = true; Todo?.Delete(it); };
        return g;
    }
}
