using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SolarisLauncher;

public partial class MainWindow
{
    private bool _passwordRevealed;
    private bool _confirmRevealed;

    private string CurrentPassword() =>
        _passwordRevealed ? PasswordVisibleText.Text : PasswordBox.Password;

    private string CurrentConfirmationPassword() =>
        _confirmRevealed ? ConfirmPasswordVisibleText.Text : ConfirmPasswordBox.Password;

    private void PasswordReveal_Click(object sender, RoutedEventArgs e) =>
        TogglePasswordReveal(false);

    private void ConfirmPasswordReveal_Click(object sender, RoutedEventArgs e) =>
        TogglePasswordReveal(true);

    private void TogglePasswordReveal(bool confirmation)
    {
        bool revealing = confirmation ? !_confirmRevealed : !_passwordRevealed;
        PasswordBox masked = confirmation ? ConfirmPasswordBox : PasswordBox;
        TextBox unmasked = confirmation ? ConfirmPasswordVisibleText : PasswordVisibleText;
        FrameworkElement open = confirmation ? ConfirmEyeOpen : PasswordEyeOpen;
        FrameworkElement closed = confirmation ? ConfirmEyeClosed : PasswordEyeClosed;
        Button toggle = confirmation ? ConfirmPasswordRevealButton : PasswordRevealButton;

        if (revealing)
        {
            unmasked.Text = masked.Password;
            unmasked.CaretIndex = unmasked.Text.Length;
            masked.Visibility = Visibility.Collapsed;
            unmasked.Visibility = Visibility.Visible;
            unmasked.Focus();
        }
        else
        {
            masked.Password = unmasked.Text;
            unmasked.Clear(); // Never retain a second plaintext copy while hidden.
            unmasked.Visibility = Visibility.Collapsed;
            masked.Visibility = Visibility.Visible;
            masked.Focus();
        }

        if (confirmation) _confirmRevealed = revealing;
        else _passwordRevealed = revealing;

        toggle.ToolTip = revealing ? "Скрыть пароль" : "Показать пароль";
        System.Windows.Automation.AutomationProperties.SetName(toggle,
            revealing ? "Скрыть пароль" : "Показать пароль");
        closed.Visibility = revealing ? Visibility.Collapsed : Visibility.Visible;
        open.Visibility = revealing ? Visibility.Visible : Visibility.Collapsed;

        if (open.RenderTransform is ScaleTransform transform)
        {
            transform.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(revealing ? 0.15 : 1.0, revealing ? 1.0 : 0.15,
                    TimeSpan.FromMilliseconds(190))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                });
        }
        UpdateCapsLockWarning();
    }

    private void ClearPasswordFields()
    {
        // Re-hide both fields whenever the player navigates between auth screens.
        if (_passwordRevealed) TogglePasswordReveal(false);
        if (_confirmRevealed) TogglePasswordReveal(true);
        PasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        PasswordVisibleText.Clear();
        ConfirmPasswordVisibleText.Clear();
        CapsLockWarning.Visibility = Visibility.Collapsed;
    }

    private void PasswordField_KeyUp(object sender, KeyEventArgs e) =>
        UpdateCapsLockWarning();

    private void PasswordField_KeyDown(object sender, KeyEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(UpdateCapsLockWarning));

    private void PasswordField_Focus(object sender, KeyboardFocusChangedEventArgs e) =>
        UpdateCapsLockWarning();

    private void UpdateCapsLockWarning() =>
        CapsLockWarning.Visibility = Keyboard.IsKeyToggled(Key.CapsLock)
            ? Visibility.Visible : Visibility.Collapsed;
}
