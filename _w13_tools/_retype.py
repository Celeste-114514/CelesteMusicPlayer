"""把补录层里 object? 成员按名字推断成准确类型（消除下游连锁错误）。"""
import re

PATH = 'CelesteMusicPlayer/LegacyWinUiShimMembers.cs'

LIST_NAMES = {'Items', 'Children', 'Points', 'Segments', 'Setters', 'GradientStops', 'Inlines',
              'SelectedItems', 'ColumnDefinitions', 'RowDefinitions', 'Keys', 'MergedDictionaries',
              'ProtectedCursors', 'ChildrenTransitions', 'KeyboardAccelerators', 'Rows', 'Columns'}
DOUBLE_NAMES = {'Value', 'Minimum', 'Maximum', 'SmallChange', 'LargeChange', 'StepFrequency',
                'FontSize', 'Width', 'Height', 'MinWidth', 'MinHeight', 'MaxWidth', 'MaxHeight',
                'ActualWidth', 'ActualHeight', 'Opacity', 'RadiusX', 'RadiusY', 'X1', 'X2', 'Y1', 'Y2',
                'StrokeThickness', 'LineHeight', 'CharacterSpacing', 'Offset', 'TintOpacity',
                'Spacing', 'ScrollOffset', 'HorizontalOffset', 'VerticalOffset', 'CenterX', 'CenterY',
                'ScaleX', 'ScaleY', 'TranslateX', 'TranslateY', 'Rotation', 'Angle', 'Progress'}
STRING_NAMES = {'Text', 'Name', 'Glyph', 'Source', 'Title', 'Subtitle', 'PlaceholderText', 'Password',
                'UriSource', 'FontFamilySource', 'Description', 'Content2'}
BOOL_NAMES = set(n for n in dir(object()) )  # placeholder
BOOL_PREFIX = ('Is', 'Has', 'Can', 'Allow', 'Enable', 'Show', 'Use', 'Auto', 'Always')
COLOR_NAMES = {'TintColor', 'FallbackColor', 'Color'}
BRUSH_NAMES = {'Background', 'Foreground', 'Fill', 'Stroke', 'BorderBrush'}
SIZE_NAMES = {'DesiredSize', 'AvailableSize', 'FinalSize', 'Size'}
DICT_NAMES = {'ThemeDictionaries', 'Resources', 'Style'}


def infer(name):
    if name in LIST_NAMES:
        return 'global::System.Collections.IList { get; } = new global::System.Collections.Generic.List<object?>()'
    if name in DOUBLE_NAMES:
        return 'double'
    if name in STRING_NAMES:
        return 'string { get; set; } = string.Empty'
    if name in COLOR_NAMES:
        return 'global::Windows.UI.Color'
    if name in BRUSH_NAMES:
        return 'global::Microsoft.UI.Xaml.Media.Brush?'
    if name in SIZE_NAMES:
        return 'global::Windows.Foundation.Size'
    if name.startswith(BOOL_PREFIX):
        return 'bool'
    return None


text = open(PATH, encoding='utf-8').read()
changed = 0


def repl(m):
    global changed
    name = m.group(1)
    t = infer(name)
    if not t:
        return m.group(0)
    changed += 1
    if '{' in t:
        return '        public %s %s;' % (t.replace(' { get; set; } = string.Empty', ''), name) if False else '        public %s %s;' % (t, name)
    return '        public %s %s { get; set; }' % (t, name)


# 只替换自动补录里 object? 形式的属性
text = re.sub(r'        public object\? (\w+) \{ get; set; \}', repl, text)
text = re.sub(r'        public static object\? (\w+) \{ get; set; \}',
              lambda m: '        public static %s %s { get; set; }' % (infer(m.group(1)) or 'object?', m.group(1)), text)
open(PATH, 'w', encoding='utf-8').write(text)
print('替换类型', changed)
