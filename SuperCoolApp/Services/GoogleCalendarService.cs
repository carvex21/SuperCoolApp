using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth;
using Google.Apis.Util.Store;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;

namespace SuperCoolApp.Services
{
    public class GoogleCalendarService
    {
        private readonly CalendarService? _svc;
        public string? UserEmail { get; }

        public GoogleCalendarService(string? credentialPath = null, string applicationName = "SuperCoolApp")
        {
            if (string.IsNullOrEmpty(credentialPath) || !File.Exists(credentialPath))
            {
                _svc = null;
                return;
            }

            try
            {
                using var stream = new FileStream(credentialPath, FileMode.Open, FileAccess.Read);
                var secrets = GoogleClientSecrets.FromStream(stream).Secrets;
                var credFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SuperCoolApp", "Google.Apis.Auth");

                var attempts = 0;
                FileDataStore? dataStore = null;
                UserCredential? userCredential = null;
                while (attempts < 2)
                {
                    attempts++;
                    try
                    {
                        dataStore = new FileDataStore(credFolder, true);
                        var scopes = new[] { CalendarService.Scope.CalendarReadonly, "openid", "email" };
                        userCredential = GoogleWebAuthorizationBroker.AuthorizeAsync(
                            secrets,
                            scopes,
                            "user",
                            CancellationToken.None,
                            dataStore,
                            new LocalServerCodeReceiver())
                            .GetAwaiter().GetResult();

                        UserEmail = null;
                        if (!string.IsNullOrEmpty(userCredential.Token.IdToken))
                        {
                            try
                            {
                                var payload = GoogleJsonWebSignature.ValidateAsync(userCredential.Token.IdToken).GetAwaiter().GetResult();
                                UserEmail = payload?.Email;
                            }
                            catch
                            {
                                UserEmail = null;
                            }
                        }

                        var userInfoFailedWith401 = false;
                        if (string.IsNullOrEmpty(UserEmail) && !string.IsNullOrEmpty(userCredential.Token.AccessToken))
                        {
                            try
                            {
                                using var http = new HttpClient();
                                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userCredential.Token.AccessToken);
                                var resp = http.GetAsync("https://openidconnect.googleapis.com/v1/userinfo").GetAwaiter().GetResult();
                                var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                                if (resp.IsSuccessStatusCode)
                                {
                                    using var doc = JsonDocument.Parse(json);
                                    if (doc.RootElement.TryGetProperty("email", out var emailProp))
                                        UserEmail = emailProp.GetString();
                                }
                                else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                                {
                                    userInfoFailedWith401 = true;
                                }
                            }
                            catch
                            {
                                // ignored
                            }
                        }

                        if (!string.IsNullOrEmpty(UserEmail))
                            break;

                        if (attempts == 1 && (userInfoFailedWith401 || string.IsNullOrEmpty(UserEmail)))
                        {
                            try
                            {
                                try { dataStore?.ClearAsync().GetAwaiter().GetResult(); }
                                catch
                                {
                                    // ignored
                                }

                                try { if (Directory.Exists(credFolder)) Directory.Delete(credFolder, true); }
                                catch
                                {
                                    // ignored
                                }

                                continue;
                            }
                            catch
                            {
                                break;
                            }
                        }

                        break;
                    }
                    catch
                    {
                        if (attempts == 1)
                        {
                            try { dataStore?.ClearAsync().GetAwaiter().GetResult(); } catch { }
                            try { if (Directory.Exists(credFolder)) Directory.Delete(credFolder, true); } catch { }
                            continue;
                        }

                        break;
                    }
                }

                if (userCredential != null)
                {
                    _svc = new CalendarService(new BaseClientService.Initializer
                    {
                        HttpClientInitializer = userCredential,
                        ApplicationName = applicationName
                    });
                }
                else
                {
                    _svc = null;
                }
            }
            catch
            {
                _svc = null;
            }
        }

        public async Task<IList<CalendarListEntry>> GetAccessibleCalendarsAsync(CancellationToken ct = default)
        {
            if (_svc == null) return new List<CalendarListEntry>();
            var results = new List<CalendarListEntry>();
            var req = _svc.CalendarList.List();
            req.ShowHidden = true;
            string? pageToken = null;
            do
            {
                req.PageToken = pageToken;
                var resp = await req.ExecuteAsync(ct);
                if (resp.Items != null) results.AddRange(resp.Items);
                pageToken = resp.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));

            return results;
        }

        public async Task<IList<(CalendarListEntry Calendar, IList<Event> Events)>> GetTodaysEventsAllCalendarsAsync(CancellationToken ct = default)
        {
            var list = new List<(CalendarListEntry, IList<Event>)>();
            if (_svc == null) return list;

            var calendars = await GetAccessibleCalendarsAsync(ct);
            var startLocal = DateTime.Today;
            var endLocal = startLocal.AddDays(1);
            var startDto = new DateTimeOffset(startLocal, TimeZoneInfo.Local.GetUtcOffset(startLocal));
            var endDto = new DateTimeOffset(endLocal, TimeZoneInfo.Local.GetUtcOffset(endLocal));

            foreach (var cal in calendars)
            {
                try
                {
                    var req = _svc.Events.List(cal.Id);
                    req.TimeMinDateTimeOffset = startDto;
                    req.TimeMaxDateTimeOffset = endDto;
                    req.ShowDeleted = false;
                    req.SingleEvents = true;
                    req.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
                    var resp = await req.ExecuteAsync(ct);
                    list.Add((cal, resp.Items ?? new List<Event>()));
                }
                catch
                {
                    list.Add((cal, new List<Event>()));
                }
            }

            return list;
        }
    }
}
