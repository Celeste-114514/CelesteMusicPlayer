namespace Microsoft.UI.Xaml.Controls
{
    public enum ContentDialogButton { None = 0, Primary = 1, Secondary = 2, Close = 3 }
    public enum ContentDialogPlacement { Popup = 0, InPlace = 1 }
}
namespace Microsoft.UI.Xaml.Media
{
    public enum PenLineJoin { Miter = 0, Bevel = 1, Round = 2 }
    public enum PenLineCap { Flat = 0, Square = 1, Round = 2, Triangle = 3 }
    public enum SweepDirection { Counterclockwise = 0, Clockwise = 1 }
    public enum FillRule { EvenOdd = 0, Nonzero = 1 }
    public enum StyleSimulations { None = 0, BoldSimulation = 1, ItalicSimulation = 2 }
}
namespace Microsoft.UI.Xaml
{
    public enum ApplicationTheme { Light = 0, Dark = 1 }
    public class SystemBackdrop : DependencyObject { }
    public class ProtectedCursor
    {
        public static ProtectedCursor? Arrow => null;
        public static ProtectedCursor? Hand => null;
        public static ProtectedCursor? IBeam => null;
        public static ProtectedCursor? Wait => null;
        public static ProtectedCursor? Cross => null;
        public static ProtectedCursor? SizeAll => null;
        public static ProtectedCursor? SizeNorthSouth => null;
        public static ProtectedCursor? SizeWestEast => null;
        public static ProtectedCursor? UniversalNo => null;
    }
}
