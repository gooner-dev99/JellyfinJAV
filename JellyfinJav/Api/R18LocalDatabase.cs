namespace JellyfinJav.Api
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Net;
    using System.Text;

    internal static class R18LocalDatabase
    {
        private const string DumpFilePattern = "r18dotdev_dump_*.sql.gz";
        private static readonly object SyncRoot = new object();
        private static DatabaseIndex? databaseIndex;

        public static (Video? Video, HttpStatusCode StatusCode) LoadVideoWithStatus(string id)
        {
            var database = GetDatabaseIndex();
            foreach (var candidate in BuildCombinedCandidates(id))
            {
                if (database.TryGetVideo(candidate, out var video))
                {
                    return (video, HttpStatusCode.OK);
                }
            }

            return (null, HttpStatusCode.NotFound);
        }

        public static (IEnumerable<VideoResult> Results, HttpStatusCode StatusCode) SearchWithStatus(string searchCode)
        {
            var (video, statusCode) = LoadVideoWithStatus(searchCode);
            if (!video.HasValue)
            {
                return (Array.Empty<VideoResult>(), statusCode);
            }

            Uri? coverUri = null;
            if (!string.IsNullOrWhiteSpace(video.Value.Cover) && Uri.TryCreate(video.Value.Cover, UriKind.Absolute, out var parsedCoverUri))
            {
                coverUri = parsedCoverUri;
            }

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

        private static DatabaseIndex GetDatabaseIndex()
        {
            if (databaseIndex != null)
            {
                return databaseIndex;
            }

            lock (SyncRoot)
            {
                if (databaseIndex == null)
                {
                    databaseIndex = LoadDatabaseIndex();
                }

                return databaseIndex;
            }
        }

        private static DatabaseIndex LoadDatabaseIndex()
        {
            var dumpPath = ResolveDumpPath();
            if (dumpPath is null)
            {
                return new DatabaseIndex();
            }

            var database = new DatabaseIndex();
            using var fileStream = File.Open(dumpPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzipStream, Encoding.UTF8, true);

            CopyBlock currentBlock = CopyBlock.None;
            string[]? currentColumns = null;

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line == "\\.")
                {
                    currentBlock = CopyBlock.None;
                    currentColumns = null;
                    continue;
                }

                if (currentBlock == CopyBlock.None)
                {
                    if (!TryParseCopyHeader(line, out currentBlock, out currentColumns))
                    {
                        continue;
                    }

                    continue;
                }

                if (currentColumns is null)
                {
                    continue;
                }

                database.AddRow(currentBlock, currentColumns, SplitCopyRow(line));
            }

            return database;
        }

        private static string? ResolveDumpPath()
        {
            var candidateRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddAncestorDirectories(candidateRoots, AppContext.BaseDirectory);
            AddAncestorDirectories(candidateRoots, Directory.GetCurrentDirectory());

            string? bestPath = null;
            DateTime bestTimestamp = DateTime.MinValue;

            foreach (var root in candidateRoots)
            {
                try
                {
                    foreach (var candidate in Directory.EnumerateFiles(root, DumpFilePattern, SearchOption.TopDirectoryOnly))
                    {
                        var timestamp = File.GetLastWriteTimeUtc(candidate);
                        if (timestamp <= bestTimestamp)
                        {
                            continue;
                        }

                        bestTimestamp = timestamp;
                        bestPath = candidate;
                    }
                }
                catch
                {
                    // Ignore directories we cannot inspect and keep searching.
                }
            }

            return bestPath;
        }

        private static void AddAncestorDirectories(ICollection<string> roots, string? startPath)
        {
            if (string.IsNullOrWhiteSpace(startPath))
            {
                return;
            }

            var current = Path.GetFullPath(startPath);
            while (!string.IsNullOrWhiteSpace(current))
            {
                roots.Add(current);

                var parent = Directory.GetParent(current);
                if (parent is null)
                {
                    break;
                }

                current = parent.FullName;
            }
        }

        private static bool TryParseCopyHeader(string line, out CopyBlock copyBlock, out string[] columns)
        {
            copyBlock = CopyBlock.None;
            columns = Array.Empty<string>();

            const string prefix = "COPY public.";
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            var openParen = line.IndexOf('(');
            var closeParen = line.IndexOf(')');
            if (openParen < 0 || closeParen < 0 || closeParen <= openParen)
            {
                return false;
            }

            var tableName = line.Substring(prefix.Length, openParen - prefix.Length).Trim();
            columns = line.Substring(openParen + 1, closeParen - openParen - 1)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            copyBlock = tableName switch
            {
                "derived_actress" => CopyBlock.DerivedActress,
                "derived_category" => CopyBlock.DerivedCategory,
                "derived_label" => CopyBlock.DerivedLabel,
                "derived_maker" => CopyBlock.DerivedMaker,
                "derived_series" => CopyBlock.DerivedSeries,
                "derived_video" => CopyBlock.DerivedVideo,
                "derived_video_actress" => CopyBlock.DerivedVideoActress,
                "derived_video_category" => CopyBlock.DerivedVideoCategory,
                _ => CopyBlock.None,
            };

            return copyBlock != CopyBlock.None;
        }

        private static string[] SplitCopyRow(string row)
        {
            var values = new List<string>();
            var current = new StringBuilder(row.Length);

            for (var index = 0; index < row.Length; index++)
            {
                var character = row[index];
                if (character == '\t')
                {
                    values.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(character);
            }

            values.Add(current.ToString());
            return values.ToArray();
        }

        private static string? UnescapeCopyValue(string value)
        {
            if (value == "\\N")
            {
                return null;
            }

            if (value.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character != '\\' || index == value.Length - 1)
                {
                    builder.Append(character);
                    continue;
                }

                index++;
                builder.Append(value[index] switch
                {
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    _ => value[index],
                });
            }

            return builder.ToString();
        }

        private static string NormalizeKey(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var character in value.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(character);
                }
            }

            return builder.ToString();
        }

        internal static string? NormalizeImageUrl(string? value)
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

        private static DateTime? ParseDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : null;
        }

        private static int? ParseInt(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        private static string NormalizeTitle(string title, IReadOnlyCollection<string> actresses)
        {
            if (actresses.Count != 1)
            {
                return title;
            }

            var actressName = actresses.First();
            var trimmedTitle = title.Trim();
            var prefixes = new[] { $"{actressName} - ", $"{actressName} -", $"{actressName} " };
            var suffixes = new[] { $" - {actressName}", $" {actressName}" };

            foreach (var prefix in prefixes)
            {
                if (!trimmedTitle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var withoutPrefix = trimmedTitle.Substring(prefix.Length).Trim();
                foreach (var suffix in suffixes)
                {
                    if (withoutPrefix.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        return withoutPrefix.Substring(0, withoutPrefix.Length - suffix.Length).Trim();
                    }
                }

                return withoutPrefix;
            }

            return title;
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

        private enum CopyBlock
        {
            None,
            DerivedActress,
            DerivedCategory,
            DerivedLabel,
            DerivedMaker,
            DerivedSeries,
            DerivedVideo,
            DerivedVideoActress,
            DerivedVideoCategory,
        }

        private sealed class DatabaseIndex
        {
            private readonly Dictionary<string, VideoRecord> videosByKey = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<int, string> actressesById = new();
            private readonly Dictionary<int, string> categoriesById = new();
            private readonly Dictionary<int, string> labelsById = new();
            private readonly Dictionary<int, string> makersById = new();
            private readonly Dictionary<int, string> seriesById = new();
            private readonly Dictionary<string, List<RelationRecord>> videoActressesByVideoId = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<RelationRecord>> videoCategoriesByVideoId = new(StringComparer.OrdinalIgnoreCase);

            public void AddRow(CopyBlock block, string[] columns, string[] values)
            {
                switch (block)
                {
                    case CopyBlock.DerivedActress:
                        this.AddActress(columns, values);
                        break;
                    case CopyBlock.DerivedCategory:
                        this.AddCategory(columns, values);
                        break;
                    case CopyBlock.DerivedLabel:
                        this.AddLabel(columns, values);
                        break;
                    case CopyBlock.DerivedMaker:
                        this.AddMaker(columns, values);
                        break;
                    case CopyBlock.DerivedSeries:
                        this.AddSeries(columns, values);
                        break;
                    case CopyBlock.DerivedVideo:
                        this.AddVideo(columns, values);
                        break;
                    case CopyBlock.DerivedVideoActress:
                        this.AddVideoRelation(this.videoActressesByVideoId, columns, values, "actress_id");
                        break;
                    case CopyBlock.DerivedVideoCategory:
                        this.AddVideoRelation(this.videoCategoriesByVideoId, columns, values, "category_id");
                        break;
                }
            }

            public bool TryGetVideo(string id, out Video video)
            {
                var normalizedKey = NormalizeKey(id);
                if (!this.videosByKey.TryGetValue(normalizedKey, out var record))
                {
                    video = default;
                    return false;
                }

                var actresses = this.ResolveRelations(this.videoActressesByVideoId, this.actressesById, record.ContentId);
                var genres = this.ResolveRelations(this.videoCategoriesByVideoId, this.categoriesById, record.ContentId)
                    .Where(genre => !string.IsNullOrWhiteSpace(genre) && !genre.Contains("sale", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                var title = !string.IsNullOrWhiteSpace(record.TitleEn) ? record.TitleEn : record.TitleJa;
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = record.DvdId;
                }

                title = NormalizeTitle(title, actresses);

                var studio = this.ResolveStudio(record.LabelId) ?? this.ResolveStudio(record.MakerId) ?? this.ResolveStudio(record.SeriesId);
                var cover = NormalizeImageUrl(record.JacketFullUrl);
                var boxArt = string.IsNullOrWhiteSpace(record.JacketThumbUrl)
                    ? string.IsNullOrWhiteSpace(cover) ? null : cover!.Replace("pl.jpg", "ps.jpg", StringComparison.OrdinalIgnoreCase)
                    : NormalizeImageUrl(record.JacketThumbUrl);

                video = new Video(
                    id: record.ContentId,
                    code: record.DvdId,
                    title: title,
                    actresses: actresses,
                    genres: genres,
                    studio: studio,
                    boxArt: boxArt,
                    cover: cover,
                    releaseDate: record.ReleaseDate);

                return true;
            }

            private void AddActress(string[] columns, string[] values)
            {
                if (!TryGetIntValue(columns, values, "id", out var id))
                {
                    return;
                }

                var name = TryGetStringValue(columns, values, "name_romaji");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    this.actressesById[id] = name;
                }
            }

            private void AddCategory(string[] columns, string[] values)
            {
                if (!TryGetIntValue(columns, values, "id", out var id))
                {
                    return;
                }

                var name = TryGetStringValue(columns, values, "name_en");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    this.categoriesById[id] = name;
                }
            }

            private void AddLabel(string[] columns, string[] values)
            {
                if (!TryGetIntValue(columns, values, "id", out var id))
                {
                    return;
                }

                var name = TryGetStringValue(columns, values, "name_en");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    this.labelsById[id] = name;
                }
            }

            private void AddMaker(string[] columns, string[] values)
            {
                if (!TryGetIntValue(columns, values, "id", out var id))
                {
                    return;
                }

                var name = TryGetStringValue(columns, values, "name_en");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    this.makersById[id] = name;
                }
            }

            private void AddSeries(string[] columns, string[] values)
            {
                if (!TryGetIntValue(columns, values, "id", out var id))
                {
                    return;
                }

                var name = TryGetStringValue(columns, values, "name_en");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    this.seriesById[id] = name;
                }
            }

            private void AddVideo(string[] columns, string[] values)
            {
                var contentId = TryGetStringValue(columns, values, "content_id");
                var dvdId = TryGetStringValue(columns, values, "dvd_id");
                if (string.IsNullOrWhiteSpace(contentId) || string.IsNullOrWhiteSpace(dvdId))
                {
                    return;
                }

                var record = new VideoRecord
                {
                    ContentId = contentId,
                    DvdId = dvdId,
                    TitleEn = TryGetStringValue(columns, values, "title_en"),
                    TitleJa = TryGetStringValue(columns, values, "title_ja"),
                    ReleaseDate = ParseDate(TryGetStringValue(columns, values, "release_date")),
                    MakerId = ParseInt(TryGetStringValue(columns, values, "maker_id")),
                    LabelId = ParseInt(TryGetStringValue(columns, values, "label_id")),
                    SeriesId = ParseInt(TryGetStringValue(columns, values, "series_id")),
                    JacketFullUrl = TryGetStringValue(columns, values, "jacket_full_url"),
                    JacketThumbUrl = TryGetStringValue(columns, values, "jacket_thumb_url"),
                };

                this.videosByKey[NormalizeKey(contentId)] = record;
                this.videosByKey[NormalizeKey(dvdId)] = record;
            }

            private void AddVideoRelation(Dictionary<string, List<RelationRecord>> relationsByVideoId, string[] columns, string[] values, string relatedIdColumnName)
            {
                var contentId = TryGetStringValue(columns, values, "content_id");
                if (string.IsNullOrWhiteSpace(contentId))
                {
                    return;
                }

                if (!TryGetIntValue(columns, values, relatedIdColumnName, out var relatedId))
                {
                    return;
                }

                int? ordinality = TryGetIntValue(columns, values, "ordinality", out var parsedOrdinality) ? parsedOrdinality : (int?)null;

                if (!relationsByVideoId.TryGetValue(NormalizeKey(contentId), out var relations))
                {
                    relations = new List<RelationRecord>();
                    relationsByVideoId[NormalizeKey(contentId)] = relations;
                }

                relations.Add(new RelationRecord(relatedId, ordinality));
            }

            private IReadOnlyList<string> ResolveRelations(Dictionary<string, List<RelationRecord>> relationsByVideoId, Dictionary<int, string> namesById, string contentId)
            {
                if (!relationsByVideoId.TryGetValue(NormalizeKey(contentId), out var relations) || relations.Count == 0)
                {
                    return Array.Empty<string>();
                }

                return relations
                    .OrderBy(relation => relation.Ordinality ?? int.MaxValue)
                    .ThenBy(relation => relation.RelatedId)
                    .Select(relation => namesById.TryGetValue(relation.RelatedId, out var name) ? name : null)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .ToArray();
            }

            private string? ResolveStudio(int? id)
            {
                if (!id.HasValue)
                {
                    return null;
                }

                if (this.labelsById.TryGetValue(id.Value, out var label))
                {
                    return label;
                }

                if (this.makersById.TryGetValue(id.Value, out var maker))
                {
                    return maker;
                }

                if (this.seriesById.TryGetValue(id.Value, out var series))
                {
                    return series;
                }

                return null;
            }

            private static bool TryGetIntValue(string[] columns, string[] values, string columnName, out int value)
            {
                var rawValue = TryGetStringValue(columns, values, columnName);
                return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            }

            private static string? TryGetStringValue(string[] columns, string[] values, string columnName)
            {
                var columnIndex = Array.FindIndex(columns, column => string.Equals(column, columnName, StringComparison.OrdinalIgnoreCase));
                if (columnIndex < 0 || columnIndex >= values.Length)
                {
                    return null;
                }

                return UnescapeCopyValue(values[columnIndex]);
            }
        }

        private readonly record struct RelationRecord(int RelatedId, int? Ordinality);

        private sealed class VideoRecord
        {
            public string ContentId { get; set; } = string.Empty;

            public string DvdId { get; set; } = string.Empty;

            public string? TitleEn { get; set; }

            public string? TitleJa { get; set; }

            public DateTime? ReleaseDate { get; set; }

            public int? MakerId { get; set; }

            public int? LabelId { get; set; }

            public int? SeriesId { get; set; }

            public string? JacketFullUrl { get; set; }

            public string? JacketThumbUrl { get; set; }
        }
    }
}