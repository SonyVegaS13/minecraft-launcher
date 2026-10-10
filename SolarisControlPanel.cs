using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SolarisLauncher;

public partial class MainWindow
{
    private sealed class SolarisAdminRow
    {
        public string Id { get; set; } = "";
        public string Nickname { get; set; } = "";
        public string Email { get; set; } = "";
        public bool IsBanned { get; set; }
        public bool EmailConfirmed { get; set; }
        public override string ToString() =>
            $"{Nickname}  ·  {Email}  ·  {(IsBanned ? "ЗАБЛОКИРОВАН" : "активен")}";
    }

    private void ControlOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_cloudSession is null || _cloudOffline || !_cloudSession.IsAdmin) return;
        SettingsView.Visibility = Visibility.Collapsed;
        ProfileView.Visibility = Visibility.Collapsed;
        ControlView.Visibility = Visibility.Visible;
        ControlStatus.Text = "SOLARIS CONTROL: для действий потребуется активная сессия с 2FA.";
        ControlAuditText.Text = "";
    }

    private void ControlBack_Click(object sender, RoutedEventArgs e)
    {
        ControlView.Visibility = Visibility.Collapsed;
        ProfileView.Visibility = Visibility.Visible;
    }

    private async void ControlSearch_Click(object sender, RoutedEventArgs e)
    {
        SolarisCloudSession? session = _cloudSession;
        if (session is null || _cloudOffline) return;
        string term = ControlSearchBox.Text.Trim();
        if (term.Length is < 2 or > 64)
        {
            ControlStatus.Text = "Введите не менее двух символов имени или email.";
            return;
        }
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using HttpResponseMessage response = await SendCloudAsync(client, session, HttpMethod.Get,
                "api/v1/control/accounts?search=" + Uri.EscapeDataString(term), null);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Нет доступа. Проверьте вход с 2FA и права администратора.");
            var rows = await response.Content.ReadFromJsonAsync<List<SolarisAdminRow>>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            ControlAccountsList.ItemsSource = rows ?? new List<SolarisAdminRow>();
            ControlStatus.Text = $"Найдено: {rows?.Count ?? 0}. Выберите игрока в списке.";
        }
        catch (Exception ex) { ControlStatus.Text = ex.Message; }
    }

    private async void ControlAccountAction_Click(object sender, RoutedEventArgs e)
    {
        SolarisCloudSession? session = _cloudSession;
        if (session is null || _cloudOffline) return;
        if (ControlAccountsList.SelectedItem is not SolarisAdminRow row ||
            sender is not Button button || button.Tag is not string action) 
        {
            ControlStatus.Text = "Сначала выберите аккаунт из результатов поиска.";
            return;
        }
        string reason = ControlReasonBox.Text.Trim();
        if (reason.Length is < 3 or > 256)
        {
            ControlStatus.Text = "Укажите причину от 3 до 256 символов.";
            return;
        }
        if (MessageBox.Show(this,
            $"Подтвердить действие «{action}» для {row.Nickname}?\nПричина: {reason}",
            "SOLARIS CONTROL", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var body = JsonContent.Create(new { reason });
            using HttpResponseMessage response = await SendCloudAsync(client, session,
                HttpMethod.Post, "api/v1/control/accounts/" +
                    Uri.EscapeDataString(row.Id) + "/" + action, body);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Сервер отклонил действие ({(int)response.StatusCode}). Проверьте разрешения.");
            ControlStatus.Text = $"Действие {action} выполнено для {row.Nickname}.";
            ControlSearch_Click(sender, e);
        }
        catch (Exception ex) { ControlStatus.Text = ex.Message; }
    }

    private async void ControlAudit_Click(object sender, RoutedEventArgs e)
    {
        SolarisCloudSession? session = _cloudSession;
        if (session is null || _cloudOffline) return;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using HttpResponseMessage response = await SendCloudAsync(client, session,
                HttpMethod.Get, "api/v1/control/audit", null);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Журнал доступен только с административной 2FA.");
            using JsonDocument audit = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            var lines = new List<string>();
            foreach (JsonElement item in audit.RootElement.EnumerateArray())
            {
                string date = item.GetProperty("utc").GetDateTimeOffset()
                    .ToLocalTime().ToString("dd.MM.yyyy HH:mm");
                lines.Add($"{date} · {item.GetProperty("operation").GetString()} · " +
                    $"target: {item.GetProperty("targetId").GetString()} · " +
                    item.GetProperty("reason").GetString());
            }
            ControlAuditText.Text = lines.Count == 0
                ? "Журнал пока пуст." : string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) { ControlStatus.Text = ex.Message; }
    }
}
