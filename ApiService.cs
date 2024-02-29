using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace MyCalendarWeb
{ 
    public class ApiService
    {

        private readonly HttpClientHandler _handler = new HttpClientHandler()
        {
            AllowAutoRedirect = false
        };

        private readonly HttpClient _httpClient;

        private const string ClientId = "student-personal-cabinet";
        private const string RedirectUri = "https://my.itmo.ru/login/callback";
        private const string Provider = "https://id.itmo.ru/auth/realms/itmo";
        private const string ApiBaseUrl = "https://my.itmo.ru/api";


        public ApiService()
        {
            _httpClient = new HttpClient(_handler);
        }

        public async Task<string> GetAccessToken(string username, string password)
        {
            var codeVerifier = GenerateCodeVerifier();
            var codeChallenge = GetCodeChallenge(codeVerifier);

            var authResponse = await _httpClient.GetAsync($"{Provider}/protocol/openid-connect/auth?" +
                $"protocol=oauth2&response_type=code&client_id={ClientId}&redirect_uri={HttpUtility.UrlEncode(RedirectUri)}&" +
                $"scope=openid&state=im_not_a_browser&code_challenge_method=S256&code_challenge={codeChallenge}");

            authResponse.EnsureSuccessStatusCode();

            var authResponseHtml = await authResponse.Content.ReadAsStringAsync();

            var formActionMatch = Regex.Match(authResponseHtml, @"<form\s+.*?\s+action=""(.*?)""", RegexOptions.Singleline);
            if (!formActionMatch.Success)
            {
                throw new Exception("Соответствие регулярному выражению действия формы Keycloak не найдено.");
            }

            var formAction = HttpUtility.HtmlDecode(formActionMatch.Groups[1].Value);
            var formData = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", username),
                new KeyValuePair<string, string>("password", password),
            });

            var formResponse = await _httpClient.PostAsync(formAction, formData);

            if (formResponse.StatusCode != System.Net.HttpStatusCode.Redirect)
            {
                throw new Exception("Неправильное имя пользователя или пароль.");
            }

            var redirectUri = formResponse.Headers.Location.ToString();
            var authCode = HttpUtility.ParseQueryString(new Uri(redirectUri).Query).Get("code");

            var tokenResponse = await _httpClient.PostAsync($"{Provider}/protocol/openid-connect/token", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("redirect_uri", RedirectUri),
                new KeyValuePair<string, string>("code", authCode),
                new KeyValuePair<string, string>("code_verifier", codeVerifier),
            }));
            tokenResponse.EnsureSuccessStatusCode();

            var tokenResponseContent = await tokenResponse.Content.ReadAsStringAsync();
            var tokenJson = JsonSerializer.Deserialize<JsonElement>(tokenResponseContent);
            var accessToken = tokenJson.GetProperty("access_token").GetString();

            return accessToken;
        }

        private static string GenerateCodeVerifier()
        {
            using var rng = RandomNumberGenerator.Create();
            var bytes = new byte[32];
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string GetCodeChallenge(string codeVerifier)
        {
            using var sha256 = SHA256.Create();
            var challengeBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(codeVerifier));
            return Convert.ToBase64String(challengeBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public async Task<IEnumerable<Dictionary<string, string>>> GetRawLessons(string accessToken, DateTime startDate, DateTime endDate)
        {
            var dateRangeParams = GetDateRangeParams(startDate, endDate);
            var calendarData = await GetCalendarData("/schedule/schedule/personal", accessToken, dateRangeParams);
            var lessons = new List<Dictionary<string, string>>();

            foreach (var day in calendarData.GetProperty("data").EnumerateArray())
            {
                var date = day.GetProperty("date").GetString();
                foreach (var lesson in day.GetProperty("lessons").EnumerateArray())
                {
                    var lessonDict = new Dictionary<string, string>
                    {
                        ["date"] = date
                    };
                    foreach (var prop in lesson.EnumerateObject())
                    {
                        lessonDict[prop.Name] = prop.Value.ToString();
                    }
                    lessons.Add(lessonDict);
                }
            }

            return lessons;
        }

        private Dictionary<string, string> GetDateRangeParams(DateTime startDate, DateTime endDate) => new Dictionary<string, string>
        {
            ["date_start"] = startDate.ToString("yyyy-MM-dd"),
            ["date_end"] = endDate.ToString("yyyy-MM-dd"),
        };

        private async Task<JsonElement> GetCalendarData(string path, string authToken, Dictionary<string, string> dateRangeParams)
        {
            var queryParams = new FormUrlEncodedContent(dateRangeParams).ReadAsStringAsync().Result;

            var url = $"{ApiBaseUrl}{path}?{queryParams}";

            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
            requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);

            var response = await _httpClient.SendAsync(requestMessage);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<JsonElement>(content);
        }
    }
}
