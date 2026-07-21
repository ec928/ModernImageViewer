using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace ModernImageViewer.VideoDirector.Views
{
    public class EmptyToVisConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is int count)
            {
                return count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class TimeSpanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is TimeSpan ts)
            {
                // Formats to hh:mm:ss.ff, stripping trailing zeros from decimals
                return ts.ToString(@"hh\:mm\:ss\.ff");
            }
            return value?.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is string s && TimeSpan.TryParse(s, out TimeSpan ts))
            {
                return ts;
            }
            return TimeSpan.Zero;
        }
    }

    public class TimeSpanToDoubleSecondsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is TimeSpan ts)
            {
                return ts.TotalSeconds;
            }
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is double d && !double.IsNaN(d))
            {
                return TimeSpan.FromSeconds(d);
            }
            return TimeSpan.Zero;
        }
    }

    public class BoolToVisConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool bValue = value is bool b && b;
            if (parameter?.ToString() == "Reverse")
            {
                bValue = !bValue;
            }
            return bValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            bool bValue = value is Visibility v && v == Visibility.Visible;
            if (parameter?.ToString() == "Reverse")
            {
                bValue = !bValue;
            }
            return bValue;
        }
    }

    public class FloatFormatConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is float f)
            {
                return f.ToString("0.##");
            }
            if (value is double d)
            {
                return d.ToString("0.##");
            }
            return value?.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class NullToVisConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value == null ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class NullToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value != null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
