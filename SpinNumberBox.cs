using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace ModernImageViewer
{
    /// <summary>
    /// A clean compact spin number box with vertically stacked up/down chevrons matching Fluent WinUI 3 styling.
    /// Provides ample breathing room between the displayed number and the spin chevrons.
    /// </summary>
    public sealed class SpinNumberBox : Grid
    {
        private readonly TextBox _textBox;
        private readonly Button _upBtn;
        private readonly Button _downBtn;
        private int _minimum = 1;
        private int _maximum = 16;
        private int _value = 1;
        private bool _isUpdating;

        public event EventHandler<int>? ValueChanged;

        /// <summary>Grid has no IsEnabled. This switches the text and the chevrons together.</summary>
        public bool IsEnabled
        {
            get => _textBox.IsEnabled;
            set
            {
                _textBox.IsEnabled = value;
                _upBtn.IsEnabled = value;
                _downBtn.IsEnabled = value;
            }
        }

        public int Minimum
        {
            get => _minimum;
            set { _minimum = value; if (_value < value) Value = value; }
        }

        public int Maximum
        {
            get => _maximum;
            set { _maximum = value; if (_value > value) Value = value; }
        }

        public int Value
        {
            get => _value;
            set
            {
                int clamped = Math.Clamp(value, _minimum, _maximum);
                if (_value == clamped && _textBox.Text == clamped.ToString()) return;
                _value = clamped;
                _isUpdating = true;
                try
                {
                    _textBox.Text = clamped.ToString();
                }
                finally
                {
                    _isUpdating = false;
                }
                ValueChanged?.Invoke(this, _value);
            }
        }

        public SpinNumberBox()
        {
            Height = 32;
            Width = 84;
            CornerRadius = new CornerRadius(4);
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x24, 0x24, 0x24));
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF));
            BorderThickness = new Thickness(1);

            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });

            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _textBox = new TextBox
            {
                Text = "1",
                FontSize = 13,
                Foreground = new SolidColorBrush(Colors.White),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 0, 4, 0),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            Grid.SetColumn(_textBox, 0);
            Grid.SetRowSpan(_textBox, 2);
            Children.Add(_textBox);

            var vDivider = new Border
            {
                BorderThickness = new Thickness(1, 0, 0, 0),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Left,
                IsHitTestVisible = false
            };
            Grid.SetColumn(vDivider, 1);
            Grid.SetRowSpan(vDivider, 2);
            Children.Add(vDivider);

            _upBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE70E", FontSize = 8, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)) },
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsTabStop = false
            };
            ToolTipService.SetToolTip(_upBtn, "Increase");
            Grid.SetColumn(_upBtn, 1);
            Grid.SetRow(_upBtn, 0);
            Children.Add(_upBtn);

            var hDivider = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false
            };
            Grid.SetColumn(hDivider, 1);
            Grid.SetRow(hDivider, 1);
            Children.Add(hDivider);

            _downBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE70D", FontSize = 8, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)) },
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0, 0, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsTabStop = false
            };
            ToolTipService.SetToolTip(_downBtn, "Decrease");
            Grid.SetColumn(_downBtn, 1);
            Grid.SetRow(_downBtn, 1);
            Children.Add(_downBtn);

            _upBtn.Click += (_, _) => Value++;
            _downBtn.Click += (_, _) => Value--;

            _textBox.TextChanged += (_, _) =>
            {
                if (_isUpdating) return;
                if (int.TryParse(_textBox.Text.Trim(), out int parsed))
                {
                    int clamped = Math.Clamp(parsed, _minimum, _maximum);
                    if (_value != clamped)
                    {
                        _value = clamped;
                        ValueChanged?.Invoke(this, _value);
                    }
                }
            };

            _textBox.LostFocus += (_, _) =>
            {
                _isUpdating = true;
                try
                {
                    _textBox.Text = _value.ToString();
                }
                finally
                {
                    _isUpdating = false;
                }
            };

            _textBox.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.Up)
                {
                    Value++;
                    e.Handled = true;
                }
                else if (e.Key == VirtualKey.Down)
                {
                    Value--;
                    e.Handled = true;
                }
            };

            PointerWheelChanged += (_, e) =>
            {
                int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
                if (delta > 0) Value++;
                else if (delta < 0) Value--;
                e.Handled = true;
            };
        }
    }
}
