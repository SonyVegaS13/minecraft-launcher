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
            using var client = NewCloudHttpClient();
            using HttpResponseMessage response = await SendCloudAsync(client, session, HttpMethod.Get,
                "api/v1/control/accounts?search=" + Uri.EscapeDataString(term), null);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Нет доступа. Проверьте вход с 2FA и права администратора.");
            var rows = await response.Content.ReadFromJsonAsync<List<SolarisAdminRow>>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (!ReferenceEquals(session, _cloudSession) || _cloudLogoutRunning) return;
            ControlAccountsList.ItemsSource = rows ?? new List<SolarisAdminRow>();
            ControlStatus.Text = $"Найдено: {rows?.Count ?? 0}. Выберите игрока в списке.";
        }
        catch (Exception ex) { if (ReferenceEquals(session, _cloudSession) && !_cloudLogoutRunning) ControlStatus.Text = ex.Message; }
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
            using var client = NewCloudHttpClient();
            using var body = JsonContent.Create(new { reason });
            using HttpResponseMessage response = await SendCloudAsync(client, session,
                HttpMethod.Post, "api/v1/control/accounts/" +
                    Uri.EscapeDataString(row.Id) + "/" + action, body);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Сервер отклонил действие ({(int)response.StatusCode}). Проверьте разрешения.");
            if (!ReferenceEquals(session, _cloudSession) || _cloudLogoutRunning) return;
            ControlStatus.Text = $"Действие {action} выполнено для {row.Nickname}.";
            ControlSearch_Click(sender, e);
        }
        catch (Exception ex) { if (ReferenceEquals(session, _cloudSession) && !_cloudLogoutRunning) ControlStatus.Text = ex.Message; }
    }

    private async void ControlAudit_Click(object sender, RoutedEventArgs e)
    {
        SolarisCloudSession? session = _cloudSession;
        if (session is null || _cloudOffline) return;
        try
        {
            using var client = NewCloudHttpClient();
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
            if (!ReferenceEquals(session, _cloudSession) || _cloudLogoutRunning) return;
            ControlAuditText.Text = lines.Count == 0
                ? "Журнал пока пуст." : string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) { if (ReferenceEquals(session, _cloudSession) && !_cloudLogoutRunning) ControlStatus.Text = ex.Message; }
    }
    private async void ControlRole_Click(object sender, RoutedEventArgs e)
    {
        SolarisCloudSession? session = _cloudSession;
        if (session is null || _cloudOffline || ControlAccountsList.SelectedItem is not SolarisAdminRow row || sender is not Button button) return;
        string reason = ControlReasonBox.Text.Trim();
        if (reason.Length is < 3 or > 256) { ControlStatus.Text = "Для смены роли укажите причину от 3 до 256 символов."; return; }
        bool enabled = (string?)button.Tag == "grant";
        if (MessageBox.Show(this, $"{(enabled ? "Назначить" : "Снять")} SolarisAdmin для {row.Nickname}?\nАдминистратор получает доступ к управлению аккаунтами. Причина будет записана в журнал.",
            "Solaris Control — права доступа", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            using var client = NewCloudHttpClient();
            using var body = JsonContent.Create(new { role = "SolarisAdmin", enabled, reason });
            using HttpResponseMessage response = await SendCloudAsync(client, session, HttpMethod.Post,
                "api/v1/control/accounts/" + Uri.EscapeDataString(row.Id) + "/roles", body);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Изменение роли отклонено: нужны административная 2FA и подтверждённый email/2FA целевого аккаунта.");
            if (!ReferenceEquals(session, _cloudSession) || _cloudLogoutRunning) return;
            ControlStatus.Text = "Роль изменена; прежние сессии целевого аккаунта отозваны.";
        }
        catch (Exception ex) { if (ReferenceEquals(session, _cloudSession) && !_cloudLogoutRunning) ControlStatus.Text = ex is InvalidOperationException ? ex.Message : "Сервис недоступен или сессия недействительна."; }
    }

    private async void ControlStatistics_Click(object sender, RoutedEventArgs e)
    {
        SolarisCloudSession? session = _cloudSession;
        if (session is null || _cloudOffline || ControlAccountsList.SelectedItem is not SolarisAdminRow row) return;
        try
        {
            using var client = NewCloudHttpClient();
            using HttpResponseMessage response = await SendCloudAsync(client, session, HttpMethod.Get,
                "api/v1/control/accounts/" + Uri.EscapeDataString(row.Id) + "/stats", null);
            response.EnsureSuccessStatusCode();
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = doc.RootElement;
            if (!ReferenceEquals(session, _cloudSession) || _cloudLogoutRunning) return;
            ControlStatus.Text = row.Nickname + " · подтверждено серверами: Vanilla " +
                DisplayDuration(data.GetProperty("vanillaSeconds").GetInt64()) + ", Modded " +
                DisplayDuration(data.GetProperty("moddedSeconds").GetInt64());
        }
        catch (Exception) { if (ReferenceEquals(session, _cloudSession) && !_cloudLogoutRunning) ControlStatus.Text = "Статистика недоступна: проверьте соединение и административную 2FA."; }
    }

    private async void ControlServiceStatus_Click(object sender, RoutedEventArgs e)
    {
        SolarisCloudSession? session = _cloudSession; if (session is null || _cloudOffline) return;
        try
        {
            using var client = NewCloudHttpClient();
            using HttpResponseMessage response = await SendCloudAsync(client, session, HttpMethod.Get, "api/v1/control/status", null);
            response.EnsureSuccessStatusCode();
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!ReferenceEquals(session, _cloudSession) || _cloudLogoutRunning) return;
            ControlStatus.Text = "Solaris ID: " + doc.RootElement.GetProperty("status").GetString() +
                " · PostgreSQL: " + doc.RootElement.GetProperty("database").GetString();
        }
        catch (Exception) { if (ReferenceEquals(session, _cloudSession) && !_cloudLogoutRunning) ControlStatus.Text = "Сервис или административный доступ недоступен."; }
    }
}
