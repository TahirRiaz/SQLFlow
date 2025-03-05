using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SQLFlowCore.Common
{
    public class ColorGenerator
    {
        private static readonly double GoldenRatioConjugate = 0.618033988749895;
        private static double _currentHue = 0.0;
        private static readonly Dictionary<string, string> ColorMapping = new Dictionary<string, string>();

        /// <summary>
        /// Converts HSL values to RGB.
        /// </summary>
        private static (int r, int g, int b) HslToRgb(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double hPrime = h / 60.0;
            double x = c * (1 - Math.Abs((hPrime % 2) - 1));
            double r1 = 0, g1 = 0, b1 = 0;

            if (hPrime < 1)
            {
                r1 = c;
                g1 = x;
                b1 = 0;
            }
            else if (hPrime < 2)
            {
                r1 = x;
                g1 = c;
                b1 = 0;
            }
            else if (hPrime < 3)
            {
                r1 = 0;
                g1 = c;
                b1 = x;
            }
            else if (hPrime < 4)
            {
                r1 = 0;
                g1 = x;
                b1 = c;
            }
            else if (hPrime < 5)
            {
                r1 = x;
                g1 = 0;
                b1 = c;
            }
            else
            {
                r1 = c;
                g1 = 0;
                b1 = x;
            }

            double m = l - c / 2;
            int r = (int)((r1 + m) * 255);
            int g = (int)((g1 + m) * 255);
            int b = (int)((b1 + m) * 255);

            return (r, g, b);
        }

        /// <summary>
        /// Converts RGB values to a hexadecimal string.
        /// </summary>
        private static string RgbToHex(int r, int g, int b)
        {
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        /// <summary>
        /// Assigns a color to a resource type in a deterministic way.
        /// </summary>
        public static string GetBlockColor(string resourceType)
        {
            resourceType = resourceType.ToLower();

            // Return existing color if already assigned
            if (ColorMapping.TryGetValue(resourceType, out string color))
            {
                return color;
            }

            // Generate a new hue using the golden ratio conjugate
            _currentHue = (_currentHue + GoldenRatioConjugate) % 1.0;
            double hueDegrees = _currentHue * 360.0;

            // Fix saturation and lightness for a balanced, professional look
            double saturation = 0.7;
            double lightness = 0.5;

            var (r, g, b) = HslToRgb(hueDegrees, saturation, lightness);
            string hexColor = RgbToHex(r, g, b);

            // Store and return the color for this resource type
            ColorMapping[resourceType] = hexColor;
            return hexColor;
        }
    }
}
