namespace Microsoft.UI { public static class Colors { private static Windows.UI.Color FromDrawing(System.Drawing.Color c) => Windows.UI.Color.FromArgb(c.A,c.R,c.G,c.B);
public static Windows.UI.Color Black => FromDrawing(System.Drawing.Color.Black);
public static Windows.UI.Color OrangeRed => FromDrawing(System.Drawing.Color.OrangeRed);
public static Windows.UI.Color Transparent => FromDrawing(System.Drawing.Color.Transparent);
public static Windows.UI.Color White => FromDrawing(System.Drawing.Color.White);
}}
