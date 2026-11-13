using System;
using System.Windows;
using System.Windows.Media;

namespace User.DafitWatch
{
    /// <summary>
    /// Icône du plugin dans le menu de gauche de SimHub, dessinée en code (24 × 24) :
    /// une montre connectée avec un cœur rouge à l'écran.
    /// </summary>
    internal static class PluginIcon
    {
        static ImageSource _image;

        public static ImageSource Image
        {
            get
            {
                if (_image == null) _image = Build();
                return _image;
            }
        }

        static ImageSource Build()
        {
            var white = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF5));
            var strap = new SolidColorBrush(Color.FromArgb(0xB0, 0xF2, 0xF3, 0xF5));
            var red   = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
            Func<Brush, double, Pen> pen = (b, w) => new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

            var g = new DrawingGroup();
            // Cadre transparent : l'image garde sa boîte de 24 × 24
            g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));

            // Bracelet haut et bas
            g.Children.Add(new GeometryDrawing(strap, null, new RectangleGeometry(new Rect(7.6, 0.8, 8.8, 3.6), 1.2, 1.2)));
            g.Children.Add(new GeometryDrawing(strap, null, new RectangleGeometry(new Rect(7.6, 19.6, 8.8, 3.6), 1.2, 1.2)));

            // Boîtier et couronne
            g.Children.Add(new GeometryDrawing(null, pen(white, 1.8), new RectangleGeometry(new Rect(4.6, 4.2, 14.8, 15.6), 3.6, 3.6)));
            g.Children.Add(new GeometryDrawing(white, null, new RectangleGeometry(new Rect(19.9, 9.6, 1.7, 4.8), 0.7, 0.7)));

            // Cœur
            var heart = Geometry.Parse(
                "M12,16.6 C10.9,15.7 7.5,13.2 7.5,10.5 C7.5,9.0 8.6,7.9 10.0,7.9 " +
                "C10.9,7.9 11.6,8.4 12,9.1 C12.4,8.4 13.1,7.9 14.0,7.9 " +
                "C15.4,7.9 16.5,9.0 16.5,10.5 C16.5,13.2 13.1,15.7 12,16.6 Z");
            g.Children.Add(new GeometryDrawing(red, null, heart));

            var image = new DrawingImage(g);
            image.Freeze();
            return image;
        }
    }
}
