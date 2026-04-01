using System;
using System.Globalization;
using Microsoft.Maui.Controls;
using Spomusic.Services;

namespace Spomusic.Converters
{
    public class ByteArrayToImageSourceConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is byte[] bytes)
            {
                try { return ImageSource.FromStream(() => new MemoryStream(bytes)); } catch { return null; }
            }
            return null;
        }
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class FavoriteColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return (bool)value! ? Color.FromArgb("#1DB954") : Colors.White;
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class IsNotNullConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value != null;
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class TimeSecondsConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            try
            {
                var seconds = System.Convert.ToDouble(value);
                var time = TimeSpan.FromSeconds(seconds);
                return time.ToString(@"mm\:ss");
            }
            catch { return "00:00"; }
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class ShuffleColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return (bool)value! ? Color.FromArgb("#1DB954") : Colors.White;
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class RepeatIconConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var mode = (RepeatMode)value!;
            return mode switch { RepeatMode.One => "🔂", _ => "🔁" };
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class RepeatColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var mode = (RepeatMode)value!;
            return mode == RepeatMode.None ? Colors.White : Color.FromArgb("#1DB954");
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class IsRepeatOneConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (RepeatMode)value! == RepeatMode.One;
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class LyricHighlightConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            // parameter will be the index from the item, value will be CurrentLyricIndex
            if (value is int currentIndex && parameter is int itemIndex)
            {
                return currentIndex == itemIndex ? Colors.White : Color.FromArgb("#44000000"); // Black with low opacity
            }
            return Color.FromArgb("#44000000");
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class LyricOpacityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is int currentIndex && parameter is int itemIndex)
            {
                return currentIndex == itemIndex ? 1.0 : 0.4;
            }
            return 0.4;
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class LyricLineColorMultiConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not int itemIndex || values[1] is not int currentIndex)
                return Color.FromArgb("#B0FFFFFF");

            bool highContrast = values.Length >= 3 && values[2] is bool hc && hc;
            if (itemIndex == currentIndex)
                return highContrast ? Colors.White : Color.FromArgb("#F8FFFC");

            var distance = Math.Abs(itemIndex - currentIndex);
            if (highContrast)
                return distance <= 1 ? Color.FromArgb("#E5FFFFFF") : Color.FromArgb("#D0FFFFFF");

            return distance <= 1 ? Color.FromArgb("#D0FFFFFF") : Color.FromArgb("#9EE4C1");
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class LyricLineOpacityMultiConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not int itemIndex || values[1] is not int currentIndex)
                return 0.82;

            bool highContrast = values.Length >= 3 && values[2] is bool hc && hc;
            if (itemIndex == currentIndex)
                return 1.0;

            var distance = Math.Abs(itemIndex - currentIndex);
            if (highContrast)
                return distance <= 1 ? 0.96 : 0.88;

            return distance <= 1 ? 0.9 : 0.78;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class LyricLineScaleMultiConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not int itemIndex || values[1] is not int currentIndex)
                return 1.0;

            if (itemIndex == currentIndex)
                return 1.07;

            var distance = Math.Abs(itemIndex - currentIndex);
            return distance <= 1 ? 1.01 : 0.98;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class HexColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string hex && !string.IsNullOrWhiteSpace(hex))
            {
                try { return Color.FromArgb(hex); } catch { }
            }

            return Color.FromArgb("#1DB954");
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class KaraokeLineFormattedMultiConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Length < 5) return new FormattedString();

            var text = values[0]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return new FormattedString();

            if (values[1] is not int itemIndex || values[2] is not int currentIndex)
                return new FormattedString { Spans = { new Span { Text = text, TextColor = Colors.White } } };

            var progress = values[3] is double p ? Math.Clamp(p, 0, 1) : 0d;
            var highContrast = values[4] is bool hc && hc;

            if (itemIndex != currentIndex)
            {
                return new FormattedString
                {
                    Spans =
                    {
                        new Span
                        {
                            Text = text,
                            FontAttributes = FontAttributes.Bold,
                            TextColor = highContrast ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#E6FFF2")
                        }
                    }
                };
            }

            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                return new FormattedString
                {
                    Spans =
                    {
                        new Span
                        {
                            Text = text,
                            FontAttributes = FontAttributes.Bold,
                            TextColor = highContrast ? Color.FromArgb("#FFF176") : Color.FromArgb("#FFF27A")
                        }
                    }
                };
            }

            var highlightedWords = Math.Clamp((int)Math.Ceiling(progress * words.Length), 0, words.Length);
            var formatted = new FormattedString();
            for (var i = 0; i < words.Length; i++)
            {
                formatted.Spans.Add(new Span
                {
                    Text = i == words.Length - 1 ? words[i] : words[i] + " ",
                    FontAttributes = FontAttributes.Bold,
                    TextColor = i < highlightedWords
                        ? (highContrast ? Color.FromArgb("#FFF176") : Color.FromArgb("#FFE85E"))
                        : (highContrast ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#CFF1DD"))
                });
            }

            return formatted;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
