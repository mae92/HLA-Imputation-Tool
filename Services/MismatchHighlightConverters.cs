
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace HLAImputation
{
    // Foreground brush for InputGrid cells
    public class InputMismatchToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string txid = value?.ToString() ?? "";
            string prop = parameter?.ToString() ?? "";

            if (Application.Current?.MainWindow is MainWindow mw)
                return mw.IsInputMismatch(txid, prop) ? Brushes.Red : Brushes.Black;

            return Brushes.Black;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    // Foreground brush for ResultGrid cells
    public class ResultMismatchToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string txid = value?.ToString() ?? "";
            string prop = parameter?.ToString() ?? "";

            if (Application.Current?.MainWindow is MainWindow mw)
                return mw.IsResultMismatch(txid, prop) ? Brushes.Red : Brushes.Black;

            return Brushes.Black;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}

