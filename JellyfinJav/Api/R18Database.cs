namespace JellyfinJav.Api
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Globalization;
    using System.Net;
    using System.Threading.Tasks;
    using Npgsql;

    internal static class R18Database
    {
        private const string ConnectionStringEnvVar = "JELLYFINJAV_R18_DB_CONNECTION_STRING";
        private const string HostEnvVar = "JELLYFINJAV_R18_DB_HOST";
        private const string PortEnvVar = "JELLYFINJAV_R18_DB_PORT";
        private const string DatabaseEnvVar = "JELLYFINJAV_R18_DB_NAME";
        private const string UserEnvVar = "JELLYFINJAV_R18_DB_USER";
        private const string PasswordEnvVar = "JELLYFINJAV_R18_DB_PASSWORD";
        private const string SslModeEnvVar = "JELLYFINJAV_R18_DB_SSL_MODE";

        private static readonly object SyncRoot = new object();
        private static NpgsqlDataSource? dataSource;
        private static bool configurationEvaluated;

        public static async Task<(Video? Video, HttpStatusCode StatusCode)> LoadVideoWithStatus(string id)
        {
            var configuredDataSource = GetConfiguredDataSource();
            if (configuredDataSource is null)
            {
                return (null, HttpStatusCode.ServiceUnavailable);
            }

            foreach (var candidate in BuildCombinedCandidates(id))
            {
                var video = await LoadVideoByCandidateAsync(configuredDataSource, candidate).ConfigureAwait(false);
                if (video.HasValue)
                {
                    return (video, HttpStatusCode.OK);
                }
            }

            return (null, HttpStatusCode.NotFound);
        }

        public static async Task<(IEnumerable<VideoResult> Results, HttpStatusCode StatusCode)> SearchWithStatus(string searchCode)
        {
            var (video, statusCode) = await LoadVideoWithStatus(searchCode).ConfigureAwait(false);
            if (!video.HasValue)
            {
                return (Array.Empty<VideoResult>(), statusCode);
            }

            var coverUri = Uri.TryCreate(video.Value.Cover, UriKind.Absolute, out var parsedCoverUri) ? parsedCoverUri : null;
            return (
                new[]
                {
                    new VideoResult
                    {
                        Code = string.IsNullOrWhiteSpace(video.Value.Code) ? searchCode.ToUpperInvariant() : video.Value.Code,
                        Id = video.Value.Id,
                        Cover = coverUri,
                    },
                },
                statusCode);
        }

        private static NpgsqlDataSource? GetConfiguredDataSource()
        {
            if (configurationEvaluated)
            {
                return dataSource;
            }

            lock (SyncRoot)
            {
                if (!configurationEvaluated)
                {
                    configurationEvaluated = true;
                    var connectionString = BuildConnectionString();
                    if (!string.IsNullOrWhiteSpace(connectionString))
                    {
                        dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
                    }
                }

                return dataSource;
            }
        }

        private static string? BuildConnectionString()
        {
            var configuredConnectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
            if (!string.IsNullOrWhiteSpace(configuredConnectionString))
            {
                return configuredConnectionString;
            }

            var host = Environment.GetEnvironmentVariable(HostEnvVar);
            var database = Environment.GetEnvironmentVariable(DatabaseEnvVar);
            var username = Environment.GetEnvironmentVariable(UserEnvVar);

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(username))
            {
                return null;
            }

            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = host,
                Database = database,
                Username = username,
                Password = Environment.GetEnvironmentVariable(PasswordEnvVar),
                Pooling = true,
                Timeout = 5,
                CommandTimeout = 15,
            };

            var portValue = Environment.GetEnvironmentVariable(PortEnvVar);
            if (int.TryParse(portValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
            {
                builder.Port = port;
            }

            var sslModeValue = Environment.GetEnvironmentVariable(SslModeEnvVar);
            if (!string.IsNullOrWhiteSpace(sslModeValue) && Enum.TryParse<SslMode>(sslModeValue, true, out var sslMode))
            {
                builder.SslMode = sslMode;
            }

            return builder.ConnectionString;
        }

        private static async Task<Video?> LoadVideoByCandidateAsync(NpgsqlDataSource dataSource, string candidate)
        {
            var normalizedKey = NormalizeKey(candidate);
            await using var connection = await dataSource.OpenConnectionAsync().ConfigureAwait(false);
            await using var videoCommand = connection.CreateCommand();
            videoCommand.CommandText = @"
                SELECT
                    v.content_id,
                    v.dvd_id,
                    COALESCE(NULLIF(v.title_en, ''), NULLIF(v.title_ja, ''), v.dvd_id) AS title,
                    v.release_date,
                    COALESCE(l.name_en, m.name_en, s.name_en) AS studio,
                    v.jacket_full_url,
                    v.jacket_thumb_url
                FROM public.derived_video v
                LEFT JOIN public.derived_label l ON l.id = v.label_id
                LEFT JOIN public.derived_maker m ON m.id = v.maker_id
                LEFT JOIN public.derived_series s ON s.id = v.series_id
                WHERE regexp_replace(lower(v.content_id), '[^a-z0-9]', '', 'g') = @key
                   OR regexp_replace(lower(v.dvd_id), '[^a-z0-9]', '', 'g') = @key
                LIMIT 1;";
            videoCommand.Parameters.AddWithValue("key", normalizedKey);

            await using var reader = await videoCommand.ExecuteReaderAsync(CommandBehavior.SingleRow).ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
            {
                return null;
            }

            var contentId = reader.GetString(0);
            var dvdId = reader.GetString(1);
            var title = reader.IsDBNull(2) ? dvdId : reader.GetString(2);
            DateTime? releaseDate = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
            var studio = reader.IsDBNull(4) ? null : reader.GetString(4);
            var cover = NormalizeImageUrl(reader.IsDBNull(5) ? null : reader.GetString(5));
            var boxArt = NormalizeImageUrl(reader.IsDBNull(6) ? null : reader.GetString(6));

            if (string.IsNullOrWhiteSpace(boxArt) && !string.IsNullOrWhiteSpace(cover))
            {
                boxArt = cover.Replace("pl.jpg", "ps.jpg", StringComparison.OrdinalIgnoreCase);
            }

            var actresses = await LoadActressesAsync(connection, contentId).ConfigureAwait(false);
            var genres = await LoadGenresAsync(connection, contentId).ConfigureAwait(false);

            return new Video(
                id: contentId,
                code: dvdId,
                title: title,
                actresses: actresses,
                genres: genres,
                studio: studio,
                boxArt: boxArt,
                cover: cover,
                releaseDate: releaseDate);
        }

        private static async Task<IReadOnlyList<string>> LoadActressesAsync(NpgsqlConnection connection, string contentId)
        {
            var actresses = new List<string>();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT a.name_romaji
                FROM public.derived_video_actress va
                INNER JOIN public.derived_actress a ON a.id = va.actress_id
                WHERE regexp_replace(lower(va.content_id), '[^a-z0-9]', '', 'g') = @contentId
                ORDER BY va.ordinality NULLS LAST, va.actress_id;";
            command.Parameters.AddWithValue("contentId", NormalizeKey(contentId));

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    var name = reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        actresses.Add(name);
                    }
                }
            }

            return actresses;
        }

        private static async Task<IReadOnlyList<string>> LoadGenresAsync(NpgsqlConnection connection, string contentId)
        {
            var genres = new List<string>();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT c.name_en
                FROM public.derived_video_category vc
                INNER JOIN public.derived_category c ON c.id = vc.category_id
                WHERE regexp_replace(lower(vc.content_id), '[^a-z0-9]', '', 'g') = @contentId
                  AND c.name_en IS NOT NULL
                  AND lower(c.name_en) NOT LIKE '%sale%'
                ORDER BY vc.release_date NULLS LAST, vc.category_id;";
            command.Parameters.AddWithValue("contentId", NormalizeKey(contentId));

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    var name = reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        genres.Add(name);
                    }
                }
            }

            return genres;
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

            var alnumOnly = NormalizeKey(lower);
            if (seen.Add(alnumOnly))
            {
                yield return alnumOnly;
            }
        }

        private static string NormalizeKey(string value)
        {
            var builder = new System.Text.StringBuilder(value.Length);
            foreach (var character in value.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(character);
                }
            }

            return builder.ToString();
        }

        private static string? NormalizeImageUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri.ToString();
            }

            var normalized = trimmed.TrimStart('/');
            if (!normalized.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            {
                normalized += ".jpg";
            }

            if (normalized.StartsWith("mono/movie/adult/", StringComparison.OrdinalIgnoreCase))
            {
                return "https://pics.dmm.co.jp/" + normalized;
            }

            return "https://awsimgsrc.dmm.com/dig/" + normalized;
        }
    }
}