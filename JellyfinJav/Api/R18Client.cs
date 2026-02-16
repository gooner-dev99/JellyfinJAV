namespace JellyfinJav.Api
{
    using Newtonsoft.Json.Linq;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    /// <summary>A web scraping client for r18.com.</summary>
    public static class R18Client
    {
        private static readonly IDictionary<string, string> CensoredWords = new Dictionary<string, string>
        {
            { "S***e", "Slave" },
            { "S*********l", "School Girl" },
            { "S********l", "Schoolgirl" },
            { "Sch**l", "School" },
            { "F***e", "Force" },
            { "F*****g", "Forcing" },
            { "P****h", "Punish" },
            { "M****t", "Molest" },
            { "S*****t", "Student" },
            { "T*****e", "Torture" },
            { "D**g", "Drug" },
            { "H*******e", "Hypnotize" },
            { "C***d", "Child" },
            { "V*****e", "Violate" },
            { "Y********l", "Young Girl" },
            { "A*****t", "Assault" },
            { "D***king", "Drinking" },
            { "D***k", "Drunk" },
            { "V*****t", "Violent" },
            { "S******g", "Sleeping" },
            { "R**e", "Rape" },
            { "R****g", "Raping" },
            { "S**t", "Scat" },
            { "K****r", "Killer" },
            { "H*******m", "Hypnotism" },
            { "G*******g", "Gangbang" },
            { "C*ck", "Cock" },
            { "K*ds", "Kids" },
            { "K****p", "Kidnap" },
            { "A****p", "Asleep" },
            { "U*********s", "Unconscious" },
            { "D******e", "Disgrace" },
            { "P********t", "Passed Out" },
            { "M************n", "Mother And Son" },
        };

        private static readonly HttpClient HttpClient = new HttpClient();

        /// <summary>Searches for a video by jav code.</summary>
        /// <param name="searchCode">The jav code. Ex: ABP-001.</param>
        /// <returns>A list of every matched video.</returns>
        public static async Task<IEnumerable<VideoResult>> Search(string searchCode)
        {
            var (results, _) = await SearchWithStatus(searchCode).ConfigureAwait(false);
            return results;
        }

        /// <summary>Searches for a video by jav code and returns the HTTP status code.</summary>
        /// <param name="searchCode">The jav code. Ex: ABP-001.</param>
        /// <returns>The matched videos and HTTP status code.</returns>
        public static async Task<(IEnumerable<VideoResult> Results, HttpStatusCode StatusCode)> SearchWithStatus(string searchCode)
        {
            var videos = new List<VideoResult>();
            var finalStatus = HttpStatusCode.NotFound;

            foreach (var candidate in BuildCombinedCandidates(searchCode))
            {
                var (video, statusCode) = await LoadVideoWithStatus(candidate).ConfigureAwait(false);
                finalStatus = statusCode;

                if (!video.HasValue)
                {
                    continue;
                }

                Uri? coverUri = null;
                if (!string.IsNullOrWhiteSpace(video.Value.Cover) && Uri.TryCreate(video.Value.Cover, UriKind.Absolute, out var parsedCoverUri))
                {
                    coverUri = parsedCoverUri;
                }

                videos.Add(new VideoResult
                {
                    Code = string.IsNullOrWhiteSpace(video.Value.Code) ? searchCode.ToUpperInvariant() : video.Value.Code,
                    Id = video.Value.Id,
                    Cover = coverUri,
                });

                break;
            }

            return (videos, finalStatus);
        }

        /// <summary>Searches for a video by jav code, and returns the first result.</summary>
        /// <param name="searchCode">The jav code. Ex: ABP-001.</param>
        /// <returns>The parsed video.</returns>
        public static async Task<Video?> SearchFirst(string searchCode)
        {
            var (video, _) = await SearchFirstWithStatus(searchCode).ConfigureAwait(false);
            return video;
        }

        /// <summary>Searches for a video by jav code and returns the first result with HTTP status code.</summary>
        /// <param name="searchCode">The jav code. Ex: ABP-001.</param>
        /// <returns>The parsed video and HTTP status code.</returns>
        public static async Task<(Video? Video, HttpStatusCode StatusCode)> SearchFirstWithStatus(string searchCode)
        {
            var (results, searchStatusCode) = await SearchWithStatus(searchCode).ConfigureAwait(false);

            if (results.Any())
            {
                return await LoadVideoWithStatus(results.FirstOrDefault().Id).ConfigureAwait(false);
            }
            else
            {
                return (null, searchStatusCode);
            }
        }

        /// <summary>Loads a video by id.</summary>
        /// <param name="id">The r18.dev unique video identifier.</param>
        /// <returns>The parsed video.</returns>
        public static async Task<Video?> LoadVideo(string id)
        {
            var (video, _) = await LoadVideoWithStatus(id).ConfigureAwait(false);
            return video;
        }

        /// <summary>Loads a video by id and returns the HTTP status code.</summary>
        /// <param name="id">The r18.dev unique video identifier.</param>
        /// <returns>The parsed video and HTTP status code.</returns>
        public static async Task<(Video? Video, HttpStatusCode StatusCode)> LoadVideoWithStatus(string id)
        {
            var finalStatus = HttpStatusCode.NotFound;

            foreach (var candidate in BuildCombinedCandidates(id))
            {
                var request = CreateJsonRequest($"https://r18.dev/videos/vod/movies/detail/-/combined={candidate}/json");
                using var response = await HttpClient.SendAsync(request).ConfigureAwait(false);
                finalStatus = response.StatusCode;

                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var jsonContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var json = JObject.Parse(jsonContent);

                var code = json["dvd_id"]?.ToString();
                var title = json["title_en"]?.ToString() ?? json["title"]?.ToString();

                if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(title))
                {
                    return (null, finalStatus);
                }

                var actresses = json["actresses"] != null
                    ? json["actresses"]
                        .Select(c => c?["name_romaji"]?.ToString())
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Select(n => n!)
                    : Enumerable.Empty<string>();

                var genres = json["categories"] != null
                    ? json["categories"]
                        .Select(c => c?["name_en"]?.ToString())
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Select(n => n!)
                    : Enumerable.Empty<string>();

                var studio = json["label_name_en"]?.ToString();
                var cover = json["jacket_full_url"]?.ToString();
                var boxArt = string.IsNullOrWhiteSpace(cover) ? null : cover.Replace("pl.jpg", "ps.jpg");
                DateTime? releaseDate = null;

                var dateString = json["release_date"]?.ToString();
                if (!string.IsNullOrWhiteSpace(dateString) && DateTime.TryParseExact(dateString, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var parsedReleaseDate))
                {
                    releaseDate = parsedReleaseDate;
                }

                title = NormalizeTitle(title, actresses);

                return (new Video(
                    id: json["content_id"]?.ToString() ?? candidate,
                    code: code,
                    title: title,
                    actresses: actresses,
                    genres: genres,
                    studio: studio,
                    boxArt: boxArt,
                    cover: cover,
                    releaseDate: releaseDate), finalStatus);
            }

            return (null, finalStatus);
        }

        private static HttpRequestMessage CreateJsonRequest(string url)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            request.Headers.TryAddWithoutValidation("Referer", "https://r18.dev/");
            request.Headers.TryAddWithoutValidation("Origin", "https://r18.dev");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36");
            return request;
        }

        private static IEnumerable<string> BuildCombinedCandidates(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
            {
                yield return trimmed;
            }

            var lower = trimmed.ToLowerInvariant();
            if (seen.Add(lower))
            {
                yield return lower;
            }

            var alnumOnly = Regex.Replace(lower, "[^a-z0-9]", string.Empty);
            if (seen.Add(alnumOnly))
            {
                yield return alnumOnly;
            }
        }

        private static string NormalizeActress(string actress)
        {
            var rx = new Regex(@"^(.+?)( ?\(.+\))?$");
            var match = rx.Match(actress);

            if (!match.Success)
            {
                return actress;
            }

            return match.Groups[1].Value;
        }

        private static string NormalizeTitle(string title, IEnumerable<string> actresses)
        {
            if (actresses.Count() != 1)
            {
                return title;
            }

            string? name = actresses.ElementAt(0);
            var rx = new Regex($"^({name} - )?(.+?)( ?-? {name})?$");
            var match = rx.Match(title);

            if (!match.Success)
            {
                return title;
            }

            return match.Groups[2].Value;
        }

        private static bool NotSaleGenre(string? genre)
        {
            var rx = new Regex(@"\bsale\b", RegexOptions.IgnoreCase);
            var match = rx.Match(genre ?? string.Empty);

            return !match.Success;
        }
    }
}