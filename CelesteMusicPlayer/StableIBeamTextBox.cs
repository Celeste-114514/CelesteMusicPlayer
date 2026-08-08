using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 固定 IBeam 光标，并在指针进出时重新断言，避免 WASDK 下被全局光标重置冲掉。
    /// </summary>
    public sealed class StableIBeamTextBox : TextBox
    {
        private static readonly InputSystemCursor IBeamCursor =
            InputSystemCursor.Create(InputSystemCursorShape.IBeam);

        public StableIBeamTextBox()
        {
            ProtectedCursor = IBeamCursor;
            PointerEntered += OnPointerAssertCursor;
            PointerMoved += OnPointerAssertCursor;
            GettingFocus += (_, _) => ProtectedCursor = IBeamCursor;
        }

        private void OnPointerAssertCursor(object sender, PointerRoutedEventArgs e)
        {
            if (!ReferenceEquals(ProtectedCursor, IBeamCursor))
            {
                ProtectedCursor = IBeamCursor;
            }
        }
    }
}
