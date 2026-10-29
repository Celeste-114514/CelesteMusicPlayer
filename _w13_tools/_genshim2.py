"""生成 W13 过渡层：非 WinUI 的控件状态容器。
MainWindow 的旧 XAML 界面已被网页取代且窗口长期隐藏，控件只承担「状态存放」作用
（DSP 各模块就是直接读写这些控件）。本层用同名命名空间提供等价的纯数据对象，
使业务代码无需改动即可脱离 Microsoft.UI.Xaml，后续再按域清理。
"""
import re

BASE_PROPS = """
        public object? Content { get; set; }
        public object? DataContext { get; set; }
        public object? Tag { get; set; }
        public string Name { get; set; } = string.Empty;
        public object? Header { get; set; }
        public object? Description { get; set; }
        public object? Title { get; set; }
        public object? Subtitle { get; set; }
        public object? Icon { get; set; }
        public object? PlaceholderText { get; set; }
        public string Text { get; set; } = string.Empty;
        public string Glyph { get; set; } = string.Empty;
        public double Value { get; set; }
        public double Minimum { get; set; }
        public double Maximum { get; set; } = 100d;
        public double SmallChange { get; set; }
        public double LargeChange { get; set; }
        public double StepFrequency { get; set; }
        public double SnapsTo { get; set; }
        public bool IsOn { get; set; }
        public bool? IsChecked { get; set; }
        public int SelectedIndex { get; set; } = -1;
        public object? SelectedItem { get; set; }
        public object? SelectedValue { get; set; }
        public object? ItemsSource { get; set; }
        public System.Collections.IList Items { get; } = new System.Collections.Generic.List<object?>();
        public System.Collections.IList Children { get; } = new System.Collections.Generic.List<object?>();
        public System.Collections.IList ColumnDefinitions { get; } = new System.Collections.Generic.List<object?>();
        public System.Collections.IList RowDefinitions { get; } = new System.Collections.Generic.List<object?>();
        public System.Collections.IList Inlines { get; } = new System.Collections.Generic.List<object?>();
        public Visibility Visibility { get; set; } = Visibility.Visible;
        public double Opacity { get; set; } = 1d;
        public bool IsEnabled { get; set; } = true;
        public bool IsHitTestVisible { get; set; } = true;
        public bool IsTabStop { get; set; } = true;
        public bool IsTapEnabled { get; set; } = true;
        public bool IsRightTapEnabled { get; set; }
        public bool IsDoubleTapEnabled { get; set; }
        public bool IsTextScaleFactorEnabled { get; set; } = true;
        public bool IsReadOnly { get; set; }
        public bool IsLoaded { get; set; } = true;
        public bool AllowDrop { get; set; }
        public bool CanDrag { get; set; }
        public bool AcceptsReturn { get; set; }
        public bool IsSpellCheckEnabled { get; set; }
        public double Width { get; set; } = double.NaN;
        public double Height { get; set; } = double.NaN;
        public double MinWidth { get; set; }
        public double MinHeight { get; set; }
        public double MaxWidth { get; set; } = double.PositiveInfinity;
        public double MaxHeight { get; set; } = double.PositiveInfinity;
        public double ActualWidth { get; set; }
        public double ActualHeight { get; set; }
        public double FontSize { get; set; } = 14d;
        public Windows.UI.Text.FontWeights FontWeight { get; set; } = new Windows.UI.Text.FontWeights();
        public FontFamily? FontFamily { get; set; }
        public TextAlignment TextAlignment { get; set; }
        public TextWrapping TextWrapping { get; set; }
        public int MaxLines { get; set; }
        public int MaxLength { get; set; }
        public int SelectionStart { get; set; }
        public int SelectionLength { get; set; }
        public string Password { get; set; } = string.Empty;
        public Thickness Margin { get; set; }
        public Thickness Padding { get; set; }
        public Thickness BorderThickness { get; set; }
        public CornerRadius CornerRadius { get; set; }
        public Brush? Background { get; set; }
        public Brush? Foreground { get; set; }
        public Brush? Fill { get; set; }
        public Brush? Stroke { get; set; }
        public double StrokeThickness { get; set; }
        public DoubleCollection? StrokeDashArray { get; set; }
        public Brush? BorderBrush { get; set; }
        public object? Shadow { get; set; }
        public System.Numerics.Vector3 Translation { get; set; }
        public System.Numerics.Vector3 CenterPoint { get; set; }
        public System.Numerics.Vector3 Scale { get; set; }
        public float Rotation { get; set; }
        public Orientation Orientation { get; set; }
        public HorizontalAlignment HorizontalAlignment { get; set; }
        public VerticalAlignment VerticalAlignment { get; set; }
        public HorizontalAlignment HorizontalContentAlignment { get; set; }
        public VerticalAlignment VerticalContentAlignment { get; set; }
        public object? Source { get; set; }
        public object? Stretch { get; set; }
        public ElementTheme RequestedTheme { get; set; }
        public ElementTheme ActualTheme { get; set; }
        public object? Style { get; set; }
        public object? Resources { get; set; }
        public object? RenderTransform { get; set; }
        public object? ContextFlyout { get; set; }
        public object? Flyout { get; set; }
        public object? Command { get; set; }
        public object? CommandParameter { get; set; }
        public int TabIndex { get; set; }
        public int Column { get; set; }
        public int Row { get; set; }
        public int ColumnSpan { get; set; } = 1;
        public int RowSpan { get; set; } = 1;
        public string? ToolTipText { get; set; }
        public double LineHeight { get; set; }
        public object? BaselineOffset { get; set; }
"""

EVENTS = [
    "Click", "Toggled", "Loaded", "Unloaded", "SizeChanged", "LayoutUpdated",
    "PointerEntered", "PointerExited", "PointerPressed", "PointerReleased", "PointerMoved",
    "PointerCaptureLost", "PointerWheelChanged", "Tapped", "DoubleTapped", "RightTapped",
    "GotFocus", "LostFocus", "KeyDown", "KeyUp", "PreviewKeyDown", "PreviewKeyUp",
    "TextChanged", "TextChanging", "SelectionChanged", "DropDownOpened", "DropDownClosed",
    "Checked", "Unchecked", "DragEnter", "DragOver", "DragLeave", "Drop",
    "ManipulationStarted", "ManipulationDelta", "ManipulationCompleted",
    "ContextRequested", "DataContextChanged", "EffectiveViewportChanged",
    "IsEnabledChanged", "ItemClick", "ChoosingItemContainer", "ContainerContentChanging",
    "ValueChanged", "SelectionChanged2",
]

METHODS = """
        public bool Focus(object? value = null) => true;
        public void UpdateLayout() { }
        public void InvalidateMeasure() { }
        public void InvalidateArrange() { }
        public void Begin() { }
        public void Stop() { }
        public void Pause() { }
        public void Resume() { }
        public void Seek(object? offset) { }
        public void SetValue(object? dp, object? value) { }
        public object? GetValue(object? dp) => null;
        public object? FindName(string name) => null;
        public void StartBringIntoView() { }
        public void StartBringIntoView(object? options) { }
        public void ScrollIntoView(object? item) { }
        public void ScrollIntoView(object? item, object? alignment) { }
        public void SelectAll() { }
        public void Select(int start, int length) { }
        public void AddHandler(object? routedEvent, object? handler, bool handledEventsToo) { }
        public void RemoveHandler(object? routedEvent, object? handler) { }
        public bool CapturePointer(object? value) => false;
        public void ReleasePointerCapture(object? value) { }
        public bool Focus2() => true;
        public object? TransformToVisual(object? visual) => null;
        public void RegisterPropertyChangedCallback(object? dp, object? callback) { }
        public void CancelDirectManipulations() { }
"""

# 控件类型：都继承基类体系
CONTROLS = [
    "Control", "ContentControl", "ButtonBase", "Button", "ToggleButton", "RepeatButton",
    "DropDownButton", "SplitButton", "HyperlinkButton", "TextBlock", "RichTextBlock",
    "RichEditBox", "TextBox", "PasswordBox", "AutoSuggestBox", "NumberBox", "Slider",
    "ToggleSwitch", "CheckBox", "RadioButton", "RadioButtons", "ComboBox", "ComboBoxItem",
    "ListBox", "ListBoxItem", "ListView", "ListViewItem", "GridView", "GridViewItem",
    "FlipView", "Grid", "StackPanel", "Canvas", "RelativePanel", "VariableSizedWrapGrid",
    "Border", "Image", "ScrollViewer", "ProgressBar", "ProgressRing", "Flyout", "MenuFlyout",
    "MenuFlyoutItem", "MenuFlyoutSeparator", "MenuBar", "MenuBarItem", "FontIcon",
    "SymbolIcon", "BitmapIcon", "IconSourceElement", "IconElement", "Panel", "ItemsControl",
    "Selector", "ContentPresenter", "ItemsPresenter", "UserControl", "Page", "Frame",
    "NavigationView", "NavigationViewItem", "NavigationViewItemHeader", "SplitView",
    "TeachingTip", "InfoBar", "Expander", "TabView", "TabViewItem", "Pivot", "PivotItem",
    "Viewbox", "PersonPicture", "SwapChainPanel", "MediaPlayerElement", "MediaElement",
    "ColumnDefinition", "RowDefinition", "Run", "Inline", "Span", "Bold", "Italic",
    "Underline", "LineBreak", "Paragraph", "TextBlock2", "ComboBox2", "GridSplitter",
    "ScrollBar", "Thumb", "TickBar", "AppBarButton", "AppBarSeparator", "CommandBar",
    "TextCommandBarFlyout", "RefreshContainer", "RefreshVisualizer", "SwipeControl",
    "TreeView", "TreeViewItem", "BreadcrumbBar", "AnimatedIcon", "AnimatedVisualPlayer",
    "ParallaxView", "RatingControl", "SplitButton2", "ColorPicker", "ColorSpectrum",
    "TextBlockHighlighter", "RichTextBlockOverflow", "ScrollContentPresenter",
]

PRIMITIVES = [
    "ButtonBase", "ToggleButton", "RepeatButton", "Thumb", "TickBar", "ScrollBar",
    "RangeBase", "SelectorItem", "ListViewItemPresenter", "GridViewItemPresenter",
    "FlyoutBase", "MenuFlyoutPresenter", "Popup", "CornerRadiusFilterConverter",
    "ToolTipServiceHelper",
]

MEDIA_TYPES = [
    "Brush", "SolidColorBrush", "LinearGradientBrush", "RadialGradientBrush", "ImageBrush",
    "AcrylicBrush", "RevealBorderBrush", "XamlCompositionBrushBase", "GradientStop",
    "FontFamily", "ThemeShadow", "CompositeTransform", "RotateTransform",
    "TranslateTransform", "ScaleTransform", "SkewTransform", "TransformGroup",
    "MatrixTransform", "Geometry", "GeometryGroup", "EllipseGeometry", "RectangleGeometry",
    "PathGeometry", "LineGeometry", "PathFigure", "PathSegment", "LineSegment",
    "ArcSegment", "BezierSegment", "ArcSegment2", "DoubleCollection", "Matrix",
    "VisualTreeHelper", "VisualStateManager", "VisualStateGroup", "VisualState",
    "VisualTransition", "Storyboard", "Timeline", "DoubleAnimation", "ColorAnimation",
    "DoubleAnimationUsingKeyFrames", "ObjectAnimationUsingKeyFrames", "PointAnimation",
    "DiscreteObjectKeyFrame", "EasingDoubleKeyFrame", "SplineDoubleKeyFrame",
    "LinearDoubleKeyFrame", "KeyTimeHelper", "EasingFunctionBase", "CubicEase",
    "QuadraticEase", "SineEase", "BackEase", "BounceEase", "CircleEase", "ElasticEase",
    "ExponentialEase", "PowerEase", "RepeatBehaviorHelper", "DurationHelper",
    "ScalarTransition", "BrushTransition", "PlaneProjection", "AlignmentX", "AlignmentY",
]

SHAPES = ["Shape", "Rectangle", "Ellipse", "Line", "Path", "Polygon", "Polyline"]

INPUT_TYPES = [
    "Pointer", "PointerPoint", "PointerRoutedEventArgs", "TappedRoutedEventArgs",
    "RightTappedRoutedEventArgs", "DoubleTappedRoutedEventArgs", "KeyRoutedEventArgs",
    "PointerEventHandler", "TappedEventHandler", "RightTappedEventHandler",
    "DoubleTappedEventHandler", "KeyEventHandler", "GettingFocusEventArgs",
    "LosingFocusEventArgs", "BringIntoViewOptions", "KeyboardAccelerator",
    "KeyboardAcceleratorInvokedEventArgs", "XamlUICommand", "StandardUICommand",
    "PointerDeviceType", "FocusManager", "InputScope", "InputScopeName",
]

XAML_TYPES = [
    "UIElement", "FrameworkElement", "DependencyObject", "DependencyProperty",
    "RoutedEventArgs", "SizeChangedEventArgs", "RoutedEventHandler", "SizeChangedEventHandler",
    "DragEventArgs", "DragEventHandler", "TextChangedEventArgs", "TextChangedEventHandler",
    "SelectionChangedEventArgs", "SelectionChangedEventHandler", "ItemClickEventArgs",
    "ItemClickEventHandler", "ContainerContentChangingEventArgs", "Window", "Application",
    "ResourceDictionary", "Style", "Setter", "DataTemplate", "ControlTemplate",
    "ItemsPanelTemplate", "Thickness", "CornerRadius", "Duration", "GridLength", "Visibility",
    "HorizontalAlignment", "VerticalAlignment", "Orientation", "TextAlignment",
    "TextWrapping", "ElementTheme", "FocusState", "GridUnitType", "LineStackingStrategy",
    "FlowDirection", "TextReadingOrder", "Stretch", "Symbol", "NavigationViewPaneDisplayMode",
    "ScrollMode", "ScrollBarVisibility", "ZoomMode", "ManipulationModes", "CornerRadius2",
    "PropertyPath", "Binding", "BindingBase", "RelativeSource", "ColorHelper",
    "XamlRoot", "WindowingHelper", "DispatcherTimer", "Deferral",
]

IMAGING = ["BitmapImage", "BitmapSource", "WriteableBitmap", "RenderTargetBitmap", "SvgImageSource"]

TEXT_TYPES = ["FontWeights"]


def gen_class(name, base):
    return "    public class %s : %s\n    {\n    }\n" % (name, base)


out = []
w = out.append
w("""// <auto-generated>
// W13 过渡层：非 WinUI 的控件状态容器。
// 旧原生界面（MainWindow.xaml）已被网页外壳取代，窗口长期隐藏，控件只作为状态载体
// （DSP 各模块直接读写它们）。本文件用同名命名空间提供等价的纯数据对象，
// 让业务代码无需改动即可脱离 Microsoft.UI.Xaml。后续按域清理时可整体删除。
// </auto-generated>
#pragma warning disable CS0436
#pragma warning disable CS0067
#pragma warning disable CS0169
#pragma warning disable CS0414
#pragma warning disable CS0108
#pragma warning disable CS0628
#pragma warning disable CS8618
#pragma warning disable CS8625
#pragma warning disable CS8600
#pragma warning disable CS8601
#pragma warning disable CS8602
#pragma warning disable CS8603
#pragma warning disable CS8604
#pragma warning disable CS0102
#pragma warning disable CS0111
#pragma warning disable CS0114
""")

# ---------- Microsoft.UI.Xaml ----------
w("namespace Microsoft.UI.Xaml\n{")
w("    public enum Visibility { Visible = 0, Collapsed = 1 }\n")
w("    public enum HorizontalAlignment { Left = 0, Center = 1, Right = 2, Stretch = 3 }\n")
w("    public enum VerticalAlignment { Top = 0, Center = 1, Bottom = 2, Stretch = 3 }\n")
w("    public enum Orientation { Vertical = 0, Horizontal = 1 }\n")
w("    public enum TextAlignment { Left = 0, Center = 1, Right = 2, Justify = 3, DetectFromContent = 4 }\n")
w("    public enum TextWrapping { NoWrap = 0, Wrap = 1, WrapWholeWords = 2 }\n")
w("    public enum ElementTheme { Default = 0, Light = 1, Dark = 2 }\n")
w("    public enum FocusState { Unfocused = 0, Pointer = 1, Keyboard = 2, Programmatic = 3 }\n")
w("    public enum GridUnitType { Auto = 0, Pixel = 1, Star = 2 }\n")
w("    public enum LineStackingStrategy { MaxHeight = 0, BlockLineHeight = 1, BaselineToBaseline = 2 }\n")
w("    public enum FlowDirection { LeftToRight = 0, RightToLeft = 1 }\n")
w("    public enum TextReadingOrder { Default = 0, UseFlowDirection = 1, DetectFromContent = 2 }\n")
w("    public enum Stretch { None = 0, Fill = 1, Uniform = 2, UniformToFill = 3 }\n")
w("    public enum Symbol { Play = 57609, Pause = 57616, Stop = 57617, Next = 57601, Previous = 57600, Setting = 57621, Accept = 57664, Cancel = 57665, Find = 57611, Refresh = 57672, More = 59181, Volume = 57675, Mute = 61440, List = 57614, Shuffle = 57690, RepeatAll = 57623, Clear = 57612, Delete = 57613, Edit = 57669, Save = 57667, OpenFile = 57680, Folder = 57624, MusicInfo = 57701, Clock = 57702, Home = 57635, Back = 57618, Forward = 57619, Up = 57636, Download = 57646, Upload = 57666, Flag = 57640, Favorite = 57628, UnFavorite = 57629, Zoom = 57677, FullScreen = 57724, View = 57641, Sort = 57615, Filter = 57610, Repair = 57716, ReportHacked = 57687 }\n")
w("""    public struct Thickness
    {
        public double Left, Top, Right, Bottom;
        public Thickness(double uniform) { Left = Top = Right = Bottom = uniform; }
        public Thickness(double left, double top, double right, double bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }
    }
    public struct CornerRadius
    {
        public double TopLeft, TopRight, BottomRight, BottomLeft;
        public CornerRadius(double uniform) { TopLeft = TopRight = BottomRight = BottomLeft = uniform; }
        public CornerRadius(double topLeft, double topRight, double bottomRight, double bottomLeft) { TopLeft = topLeft; TopRight = topRight; BottomRight = bottomRight; BottomLeft = bottomLeft; }
    }
    public struct Duration
    {
        public System.TimeSpan TimeSpan;
        public Duration(System.TimeSpan value) { TimeSpan = value; }
        public static Duration Automatic => new Duration(System.TimeSpan.Zero);
        public static Duration Forever => new Duration(System.TimeSpan.MaxValue);
    }
    public struct GridLength
    {
        public double Value;
        public GridUnitType GridUnitType;
        public GridLength(double value) { Value = value; GridUnitType = GridUnitType.Pixel; }
        public GridLength(double value, GridUnitType type) { Value = value; GridUnitType = type; }
        public static GridLength Auto => new GridLength(1, GridUnitType.Auto);
    }
    public class RoutedEventArgs : System.EventArgs { public object? OriginalSource { get; set; } public bool Handled { get; set; } }
    public class SizeChangedEventArgs : RoutedEventArgs { public System.Windows.Size PreviousSize { get; set; } public System.Windows.Size NewSize { get; set; } }
    public delegate void RoutedEventHandler(object sender, RoutedEventArgs e);
    public delegate void SizeChangedEventHandler(object sender, SizeChangedEventArgs e);
    public class DragEventArgs : RoutedEventArgs { public bool AcceptedOperation { get; set; } public object? DataView { get; set; } public System.Windows.Point GetPosition(object? relativeTo) => default; }
    public delegate void DragEventHandler(object sender, DragEventArgs e);
    public class TextChangedEventArgs : RoutedEventArgs { }
    public delegate void TextChangedEventHandler(object sender, TextChangedEventArgs e);
    public class SelectionChangedEventArgs : RoutedEventArgs { public System.Collections.IList AddedItems { get; } = new System.Collections.Generic.List<object?>(); public System.Collections.IList RemovedItems { get; } = new System.Collections.Generic.List<object?>(); }
    public delegate void SelectionChangedEventHandler(object sender, SelectionChangedEventArgs e);
    public class ItemClickEventArgs : RoutedEventArgs { public object? ClickedItem { get; set; } }
    public delegate void ItemClickEventHandler(object sender, ItemClickEventArgs e);
    public class ContainerContentChangingEventArgs : RoutedEventArgs { public object? Item { get; set; } public object? ItemContainer { get; set; } public bool InRecycleQueue { get; set; } }
    public class DependencyProperty { public static object? UnsetValue => null; }
""")
w("""    public class DependencyObject
    {
        public object? GetValue(DependencyProperty? dp) => null;
        public void SetValue(DependencyProperty? dp, object? value) { }
        public void ClearValue(DependencyProperty? dp) { }
        public object? ReadLocalValue(DependencyProperty? dp) => null;
        public void RegisterPropertyChangedCallback(DependencyProperty? dp, object? callback) { }
        public void CancelAnimation(DependencyProperty? dp) { }
    }
""")
# UIElement 基类
w("    public class UIElement : DependencyObject\n    {")
w(BASE_PROPS)
for ev in EVENTS:
    evn = ev.rstrip("2")
    w("        public event RoutedEventHandler? %s;\n" % evn)
w(METHODS)
w("        public void RaiseAll() { }\n")
w("    }\n")
w("    public class FrameworkElement : UIElement\n    {\n")
w("        public double ActualOffset { get; set; }\n")
w("        public object? Parent { get; set; }\n")
w("        public object? Tapped2 { get; set; }\n")
w("        public static readonly DependencyProperty? VisibilityProperty = null;\n")
w("        public static readonly DependencyProperty? ForegroundProperty = null;\n")
w("        public static readonly DependencyProperty? BackgroundProperty = null;\n")
w("        public static readonly DependencyProperty? FontFamilyProperty = null;\n")
w("        public static readonly DependencyProperty? FontSizeProperty = null;\n")
w("        public static readonly DependencyProperty? FontWeightProperty = null;\n")
w("        public static readonly DependencyProperty? OpacityProperty = null;\n")
w("        public static readonly DependencyProperty? TagProperty = null;\n")
w("        public static readonly DependencyProperty? MarginProperty = null;\n")
w("        public static readonly DependencyProperty? WidthProperty = null;\n")
w("        public static readonly DependencyProperty? HeightProperty = null;\n")
w("        public static readonly DependencyProperty? TextProperty = null;\n")
w("        public static readonly DependencyProperty? ContentProperty = null;\n")
w("        public static readonly DependencyProperty? IsEnabledProperty = null;\n")
w("    }\n")
for t in ["Window", "Application", "ResourceDictionary", "Style", "Setter", "DataTemplate",
          "ControlTemplate", "ItemsPanelTemplate", "PropertyPath", "Binding", "BindingBase",
          "RelativeSource", "ColorHelper", "XamlRoot", "WindowingHelper", "DispatcherTimer",
          "Deferral"]:
    w(gen_class(t, "FrameworkElement" if t in ("Window", "Application") else "DependencyObject"))
w("""    public class DispatcherTimer : DependencyObject
    {
        public System.TimeSpan Interval { get; set; }
        public bool IsEnabled { get; set; }
        public event System.EventHandler<object>? Tick;
        public void Start() { }
        public void Stop() { }
    }
""")
w("}\n")

# ---------- Microsoft.UI.Xaml.Controls ----------
w("namespace Microsoft.UI.Xaml.Controls\n{")
w(gen_class("Control", "Microsoft.UI.Xaml.FrameworkElement"))
w(gen_class("ContentControl", "Control"))
w(gen_class("Panel", "Microsoft.UI.Xaml.FrameworkElement"))
w(gen_class("ItemsControl", "Control"))
w(gen_class("Selector", "ItemsControl"))
w(gen_class("ButtonBase", "ContentControl"))


def grid_helpers():
    return """
        public static int GetColumn(Microsoft.UI.Xaml.FrameworkElement? element) => 0;
        public static void SetColumn(Microsoft.UI.Xaml.FrameworkElement? element, int value) { }
        public static int GetRow(Microsoft.UI.Xaml.FrameworkElement? element) => 0;
        public static void SetRow(Microsoft.UI.Xaml.FrameworkElement? element, int value) { }
        public static int GetColumnSpan(Microsoft.UI.Xaml.FrameworkElement? element) => 1;
        public static void SetColumnSpan(Microsoft.UI.Xaml.FrameworkElement? element, int value) { }
        public static int GetRowSpan(Microsoft.UI.Xaml.FrameworkElement? element) => 1;
        public static void SetRowSpan(Microsoft.UI.Xaml.FrameworkElement? element, int value) { }
"""


skip = {"Control", "ContentControl", "Panel", "ItemsControl", "Selector", "ButtonBase"}
for t in CONTROLS:
    if t in skip:
        continue
    base = "Control"
    if t in ("TextBlock", "RichTextBlock", "RichTextBlockOverflow", "Border", "Image",
             "Run", "Inline", "Span", "Bold", "Italic", "Underline", "LineBreak", "Paragraph",
             "IconElement", "TextBlock2", "SwapChainPanel", "MediaPlayerElement", "MediaElement",
             "Viewbox", "ParallaxView", "GridSplitter", "RefreshVisualizer", "AnimatedIcon",
             "AnimatedVisualPlayer", "TextBlockHighlighter"):
        base = "Microsoft.UI.Xaml.FrameworkElement"
    elif t in ("Grid", "StackPanel", "Canvas", "RelativePanel", "VariableSizedWrapGrid",
               "GridSplitter2"):
        base = "Panel"
    elif t in ("ListView", "GridView", "ListBox", "FlipView", "TreeView", "ComboBox",
               "RadioButtons", "ListBox2"):
        base = "Selector"
    elif t in ("ComboBoxItem", "ListViewItem", "GridViewItem", "ListBoxItem", "TreeViewItem",
               "PivotItem", "TabViewItem", "NavigationViewItem", "MenuFlyoutItem",
               "MenuFlyoutSeparator", "NavigationViewItemHeader", "MenuBarItem"):
        base = "ContentControl"
    elif t in ("ColumnDefinition", "RowDefinition"):
        base = "Microsoft.UI.Xaml.DependencyObject"
    elif t in ("FontIcon", "SymbolIcon", "BitmapIcon", "IconSourceElement"):
        base = "IconElement"
    elif t in ("Flyout", "MenuFlyout"):
        base = "Microsoft.UI.Xaml.DependencyObject"
    elif t in ("UserControl", "Page"):
        base = "ContentControl"
    elif t in ("ContentPresenter", "ItemsPresenter", "ScrollContentPresenter"):
        base = "Microsoft.UI.Xaml.FrameworkElement"
    extra = grid_helpers() if t == "Grid" else ""
    w("    public class %s : %s\n    {\n%s    }\n" % (t, base, extra))
w("    public class IconElement : Microsoft.UI.Xaml.FrameworkElement\n    {\n    }\n")
w("""    public class ToolTipService
    {
        public static void SetToolTip(Microsoft.UI.Xaml.DependencyObject? element, object? value) { }
        public static object? GetToolTip(Microsoft.UI.Xaml.DependencyObject? element) => null;
        public static readonly Microsoft.UI.Xaml.DependencyProperty? ToolTipProperty = null;
    }
""")
w("    public enum Symbol { Play = 57609, Pause = 57616, Stop = 57617, Next = 57601, Previous = 57600, Setting = 57621, Accept = 57664, Cancel = 57665, Find = 57611, Refresh = 57672, More = 59181, Volume = 57675, Mute = 61440, List = 57614, Shuffle = 57690, RepeatAll = 57623, Clear = 57612, Delete = 57613, Edit = 57669, Save = 57667, OpenFile = 57680, Folder = 57624, MusicInfo = 57701, Clock = 57702, Home = 57635, Back = 57618, Forward = 57619, Up = 57636, Download = 57646, Upload = 57666, Flag = 57640, Favorite = 57628, UnFavorite = 57629, Zoom = 57677, FullScreen = 57724, View = 57641, Sort = 57615, Filter = 57610, Repair = 57716 }\n")
w("    public enum ScrollMode { Auto = 0, Enabled = 1, Disabled = 2 }\n")
w("    public enum ScrollBarVisibility { Auto = 0, Visible = 1, Hidden = 2, Collapsed = 3, Default = 0 }\n")
w("    public enum ZoomMode { Auto = 0, Enabled = 1, Disabled = 2 }\n")
w("    public enum NavigationViewPaneDisplayMode { Auto = 0, Left = 1, Top = 2, LeftCompact = 3, LeftMinimal = 4 }\n")
w("    public enum InfoBarSeverity { Informational = 0, Success = 1, Warning = 2, Error = 3 }\n")
w("    public enum TeachingTipPlacementMode { Auto = 0, Top = 1, Bottom = 2, Left = 3, Right = 4, TopLeft = 5, TopRight = 6, BottomLeft = 7, BottomRight = 8, LeftTop = 9, LeftBottom = 10, RightTop = 11, RightBottom = 12, Center = 13 }\n")
w("    public enum FlyoutPlacementMode { Auto = 0, Top = 1, Bottom = 2, Left = 3, Right = 4, Full = 5, TopEdgeAlignedLeft = 6, TopEdgeAlignedRight = 7, BottomEdgeAlignedLeft = 8, BottomEdgeAlignedRight = 9, LeftEdgeAlignedTop = 10, LeftEdgeAlignedBottom = 11, RightEdgeAlignedTop = 12, RightEdgeAlignedBottom = 13 }\n")
w("    public enum ContentDialogResult { None = 0, Primary = 1, Secondary = 2 }\n")
w("    public sealed class ContentDialog : ContentControl\n    {\n        public object? PrimaryButtonText { get; set; }\n        public object? SecondaryButtonText { get; set; }\n        public object? CloseButtonText { get; set; }\n        public ContentDialogResult Result { get; set; }\n        public object? Title2 { get; set; }\n        public System.Threading.Tasks.Task<ContentDialogResult> ShowAsync() => System.Threading.Tasks.Task.FromResult(ContentDialogResult.None);\n        public void Hide() { }\n    }\n")
w("    public class TeachingTip : ContentControl\n    {\n        public bool IsOpen { get; set; }\n        public TeachingTipPlacementMode PreferredPlacement { get; set; }\n        public object? Target { get; set; }\n        public void IsOpen2(bool v) { IsOpen = v; }\n    }\n")
w("    public class InfoBadge : Microsoft.UI.Xaml.FrameworkElement\n    {\n        public double Value { get; set; }\n    }\n")
w("    public class TextBlock2 : Microsoft.UI.Xaml.FrameworkElement\n    {\n    }\n")
w("}\n")

# ---------- Microsoft.UI.Xaml.Controls.Primitives ----------
w("namespace Microsoft.UI.Xaml.Controls.Primitives\n{")
for t in PRIMITIVES:
    base = "Microsoft.UI.Xaml.Controls.Control"
    if t in ("Thumb", "TickBar", "RangeBase"):
        base = "Microsoft.UI.Xaml.Controls.Control"
    if t in ("FlyoutBase", "Popup"):
        base = "Microsoft.UI.Xaml.DependencyObject"
    if t in ("SelectorItem", "ListViewItemPresenter", "GridViewItemPresenter"):
        base = "Microsoft.UI.Xaml.Controls.ContentControl"
    w(gen_class(t, base))
w("}\n")

# ---------- Microsoft.UI.Xaml.Media ----------
w("namespace Microsoft.UI.Xaml.Media\n{")
w("    public class Brush : Microsoft.UI.Xaml.DependencyObject\n    {\n        public double Opacity { get; set; } = 1d;\n    }\n")
w("    public class SolidColorBrush : Brush\n    {\n        public Windows.UI.Color Color { get; set; }\n        public SolidColorBrush() { }\n        public SolidColorBrush(Windows.UI.Color color) { Color = color; }\n    }\n")
w("    public class GradientStop : Microsoft.UI.Xaml.DependencyObject\n    {\n        public Windows.UI.Color Color { get; set; }\n        public double Offset { get; set; }\n    }\n")
w("    public class LinearGradientBrush : Brush\n    {\n        public System.Collections.IList GradientStops { get; } = new System.Collections.Generic.List<object?>();\n        public double StartPointX { get; set; }\n    }\n")
w(gen_class("RadialGradientBrush", "Brush"))
w(gen_class("ImageBrush", "Brush"))
w(gen_class("AcrylicBrush", "Brush"))
w(gen_class("RevealBorderBrush", "Brush"))
w(gen_class("XamlCompositionBrushBase", "Brush"))
w("    public class FontFamily : Microsoft.UI.Xaml.DependencyObject\n    {\n        public string Source { get; set; } = string.Empty;\n        public FontFamily() { }\n        public FontFamily(string source) { Source = source; }\n    }\n")
w(gen_class("ThemeShadow", "Microsoft.UI.Xaml.DependencyObject"))
for t in ["CompositeTransform", "RotateTransform", "TranslateTransform", "ScaleTransform",
          "SkewTransform", "TransformGroup", "MatrixTransform", "PlaneProjection"]:
    w(gen_class(t, "Microsoft.UI.Xaml.DependencyObject"))
for t in ["Geometry", "GeometryGroup", "EllipseGeometry", "RectangleGeometry", "PathGeometry",
          "LineGeometry"]:
    w(gen_class(t, "Microsoft.UI.Xaml.DependencyObject"))
for t in ["PathFigure", "PathSegment", "LineSegment", "ArcSegment", "BezierSegment"]:
    w(gen_class(t, "Microsoft.UI.Xaml.DependencyObject"))
w("    public class DoubleCollection : System.Collections.Generic.List<double>\n    {\n    }\n")
w("    public struct Matrix { public double M11, M12, M21, M22, OffsetX, OffsetY; }\n")
w("""    public static class VisualTreeHelper
    {
        public static Microsoft.UI.Xaml.DependencyObject? GetParent(Microsoft.UI.Xaml.DependencyObject? reference) => null;
        public static Microsoft.UI.Xaml.DependencyObject? GetChild(Microsoft.UI.Xaml.DependencyObject? reference, int index) => null;
        public static int GetChildrenCount(Microsoft.UI.Xaml.DependencyObject? reference) => 0;
        public static void DisconnectChildrenRecursive(Microsoft.UI.Xaml.DependencyObject? element) { }
    }
""")
w("    public static class VisualStateManager\n    {\n        public static bool GoToState(Microsoft.UI.Xaml.Controls.Control? control, string stateName, bool useTransitions) => false;\n        public static System.Collections.IList GetVisualStateGroups(Microsoft.UI.Xaml.FrameworkElement? obj) => new System.Collections.Generic.List<object?>();\n    }\n")
for t in ["VisualStateGroup", "VisualState", "VisualTransition"]:
    w(gen_class(t, "Microsoft.UI.Xaml.DependencyObject"))
w("    public class Storyboard : Microsoft.UI.Xaml.DependencyObject\n    {\n        public System.Collections.IList Children { get; } = new System.Collections.Generic.List<object?>();\n        public void Begin() { }\n        public void Stop() { }\n        public void Pause() { }\n        public void Resume() { }\n        public void Seek(System.TimeSpan offset) { }\n        public static void SetTarget(object? timeline, object? target) { }\n        public static object? GetTarget(object? timeline) => null;\n        public static void SetTargetProperty(object? timeline, string path) { }\n        public static string? GetTargetProperty(object? timeline) => null;\n        public static string? GetTargetName(object? timeline) => null;\n        public static void SetTargetName(object? timeline, string name) { }\n    }\n")
for t in ["Timeline", "DoubleAnimation", "ColorAnimation", "DoubleAnimationUsingKeyFrames",
          "ObjectAnimationUsingKeyFrames", "PointAnimation", "DiscreteObjectKeyFrame",
          "EasingDoubleKeyFrame", "SplineDoubleKeyFrame", "LinearDoubleKeyFrame"]:
    w(gen_class(t, "Microsoft.UI.Xaml.DependencyObject"))
for t in ["EasingFunctionBase", "CubicEase", "QuadraticEase", "SineEase", "BackEase",
          "BounceEase", "CircleEase", "ElasticEase", "ExponentialEase", "PowerEase"]:
    w(gen_class(t, "Microsoft.UI.Xaml.DependencyObject"))
w("    public static class EasingFunctionBase2 { }\n")
w("    public static class DurationHelper\n    {\n        public static Microsoft.UI.Xaml.Duration FromTimeSpan(System.TimeSpan ts) => new Microsoft.UI.Xaml.Duration(ts);\n        public static Microsoft.UI.Xaml.Duration Automatic => Microsoft.UI.Xaml.Duration.Automatic;\n    }\n")
w("    public struct KeyTime { public System.TimeSpan TimeSpan; public static KeyTime FromTimeSpan(System.TimeSpan ts) => new KeyTime { TimeSpan = ts }; }\n")
w("    public static class KeyTimeHelper { public static KeyTime FromTimeSpan(System.TimeSpan ts) => KeyTime.FromTimeSpan(ts); }\n")
w("    public enum AlignmentX { Left = 0, Center = 1, Right = 2 }\n")
w("    public enum AlignmentY { Top = 0, Center = 1, Bottom = 2 }\n")
w("    public enum EasingMode { Default = 0, EaseOut = 1, EaseIn = 2, EaseInOut = 3 }\n")
w("    public enum RepeatBehavior { }\n")
w("""    public class AcrylicBackgroundFill { }
    public class ScalarTransition : Microsoft.UI.Xaml.DependencyObject { public Microsoft.UI.Xaml.Duration Duration { get; set; } }
    public class BrushTransition : Microsoft.UI.Xaml.DependencyObject { public Microsoft.UI.Xaml.Duration Duration { get; set; } }
""")
w("}\n")

# ---------- Microsoft.UI.Xaml.Media.Imaging ----------
w("namespace Microsoft.UI.Xaml.Media.Imaging\n{")
for t in IMAGING:
    base = "Microsoft.UI.Xaml.Media.ImageSource" if t != "ImageSource" else "Microsoft.UI.Xaml.DependencyObject"
    w(gen_class(t, "Microsoft.UI.Xaml.DependencyObject"))
w("}\n")
w("namespace Microsoft.UI.Xaml.Media\n{\n    public class ImageSource : Microsoft.UI.Xaml.DependencyObject\n    {\n    }\n}\n")

# ---------- Microsoft.UI.Xaml.Shapes ----------
w("namespace Microsoft.UI.Xaml.Shapes\n{")
for t in SHAPES:
    w(gen_class(t, "Microsoft.UI.Xaml.FrameworkElement" if t == "Shape" else "Shape"))
w("}\n")

# ---------- Microsoft.UI.Xaml.Input ----------
w("namespace Microsoft.UI.Xaml.Input\n{")
w("    public class PointerRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs\n    {\n        public System.Windows.Point GetCurrentPoint(object? relativeTo) => default;\n        public object? Pointer { get; set; }\n        public bool Handled2 { get; set; }\n        public int MouseWheelDelta { get; set; }\n    }\n")
w("    public delegate void PointerEventHandler(object sender, PointerRoutedEventArgs e);\n")
w("    public class TappedRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs\n    {\n        public System.Windows.Point GetPosition(object? relativeTo) => default;\n    }\n")
w("    public delegate void TappedEventHandler(object sender, TappedRoutedEventArgs e);\n")
w("    public class RightTappedRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs\n    {\n        public System.Windows.Point GetPosition(object? relativeTo) => default;\n    }\n")
w("    public delegate void RightTappedEventHandler(object sender, RightTappedRoutedEventArgs e);\n")
w("    public class DoubleTappedRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs\n    {\n        public System.Windows.Point GetPosition(object? relativeTo) => default;\n    }\n")
w("    public delegate void DoubleTappedEventHandler(object sender, DoubleTappedRoutedEventArgs e);\n")
w("    public class KeyRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs\n    {\n        public object? Key { get; set; }\n        public string? OriginalKey { get; set; }\n    }\n")
w("    public delegate void KeyEventHandler(object sender, KeyRoutedEventArgs e);\n")
for t in ["Pointer", "PointerPoint", "GettingFocusEventArgs", "LosingFocusEventArgs",
          "BringIntoViewOptions", "KeyboardAccelerator", "KeyboardAcceleratorInvokedEventArgs",
          "XamlUICommand", "StandardUICommand", "InputScope", "InputScopeName"]:
    w(gen_class(t, "Microsoft.UI.Xaml.DependencyObject"))
w("    public enum PointerDeviceType { Touch = 0, Pen = 1, Mouse = 2 }\n")
w("    public static class FocusManager\n    {\n        public static object? GetFocusedElement() => null;\n        public static bool TryMoveFocus(object? direction) => false;\n    }\n")
w("}\n")

# ---------- Microsoft.UI.Xaml.Data ----------
w("namespace Microsoft.UI.Xaml.Data\n{\n    public class Binding : Microsoft.UI.Xaml.DependencyObject\n    {\n        public object? Path { get; set; }\n        public object? Source { get; set; }\n        public object? Mode { get; set; }\n        public object? Converter { get; set; }\n        public object? ConverterParameter { get; set; }\n        public object? UpdateSourceTrigger { get; set; }\n    }\n    public enum BindingMode { OneWay = 1, OneTime = 2, TwoWay = 3 }\n    public enum UpdateSourceTrigger { Default = 0, PropertyChanged = 1, LostFocus = 2, Explicit = 3 }\n}\n")

# ---------- Microsoft.UI.Xaml.Navigation / Documents ----------
w("namespace Microsoft.UI.Xaml.Navigation\n{\n    public class NavigationEventArgs : Microsoft.UI.Xaml.RoutedEventArgs\n    {\n        public object? Parameter { get; set; }\n        public object? SourcePageType { get; set; }\n    }\n}\n")
w("namespace Microsoft.UI.Xaml.Documents\n{\n")
for t in ["Run", "Span", "Bold", "Italic", "Underline", "LineBreak", "Paragraph", "Hyperlink", "Inline"]:
    w(gen_class(t, "Microsoft.UI.Xaml.DependencyObject"))
w("}\n")

# ---------- Microsoft.UI.Text ----------
w("namespace Microsoft.UI.Text\n{\n    public struct FontWeights\n    {\n        public int Weight;\n        public static FontWeights Normal => new FontWeights { Weight = 400 };\n        public static FontWeights Bold => new FontWeights { Weight = 700 };\n        public static FontWeights Medium => new FontWeights { Weight = 500 };\n        public static FontWeights SemiBold => new FontWeights { Weight = 600 };\n        public static FontWeights Light => new FontWeights { Weight = 300 };\n        public static FontWeights SemiLight => new FontWeights { Weight = 350 };\n        public static FontWeights ExtraBold => new FontWeights { Weight = 800 };\n        public static FontWeights Black => new FontWeights { Weight = 900 };\n        public static FontWeights Thin => new FontWeights { Weight = 100 };\n        public static FontWeights ExtraLight => new FontWeights { Weight = 200 };\n    }\n    public enum TextSetOptions { None = 0, UnicodeBidi = 1, Unlink = 8, Unhide = 16 }\n    public enum TextGetOptions { None = 0, AdjustCrlf = 1, UseCrlf = 2, UseObjectText = 4, NoHiddenText = 8 }\n}\n")

open('LegacyWinUiShim.cs', 'w', encoding='utf-8').write('\n'.join(out))
print('生成 LegacyWinUiShim.cs 行数:', len('\n'.join(out).splitlines()))
