namespace JellyfinJav.Providers.R18Provider
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Data.Enums;
    using JellyfinJav.Api;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;
    using Microsoft.Extensions.Logging;

    /// <summary>The provider for R18 videos.</summary>
    public class R18Provider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
    {
        private static readonly HttpClient HttpClient = new HttpClient();
        private readonly ILibraryManager libraryManager;
        private readonly ILogger<R18Provider> logger;

#pragma warning disable SA1614 // Element parameter documentation should have text
        /// <summary>
        /// Initializes a new instance of the <see cref="R18Provider"/> class.
        /// </summary>
        /// <param name="libraryManager"></param>
        /// <param name="logger"></param>
        public R18Provider(ILibraryManager libraryManager, ILogger<R18Provider> logger)
#pragma warning restore SA1614 // Element parameter documentation should have text
        {
            this.libraryManager = libraryManager;
            this.logger = logger;
        }

        /// <inheritdoc />
        public string Name => "R18";

        /// <inheritdoc />
        public int Order => 99;

        /// <inheritdoc />
        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancelToken)
        {
            var originalTitle = Utility.GetVideoOriginalTitle(info, this.libraryManager);
            info.Name = originalTitle;

            this.logger.LogInformation("[JellyfinJav] R18 - originalTitle: " + originalTitle);

            Api.Video? video;
            if (info.ProviderIds.ContainsKey("R18"))
            {
                var r18Id = info.ProviderIds["R18"];
                this.logger.LogInformation("[JellyfinJav] R18 - Metadata lookup by provider id: " + r18Id);
                var (videoById, metadataStatusCodeById) = await R18Client.LoadVideoWithStatus(r18Id).ConfigureAwait(false);
                video = videoById;
                this.logger.LogInformation("[JellyfinJav] R18 - Metadata lookup response for provider id {R18Id}: HTTP {StatusCode} ({StatusName}), has value: {HasValue}", r18Id, (int)metadataStatusCodeById, metadataStatusCodeById, video.HasValue);
            }
            else
            {
                var code = Utility.ExtractCodeFromFilename(originalTitle);
                if (code is null)
                {
                    this.logger.LogInformation("[JellyfinJav] R18 - Code is NULL " + code + "|" + originalTitle);
                    return new MetadataResult<Movie>();
                }

                var (videoBySearch, metadataStatusCodeBySearch) = await R18Client.SearchFirstWithStatus(code).ConfigureAwait(false);
                video = videoBySearch;
                this.logger.LogInformation("[JellyfinJav] R18 - SearchFirst response for code {Code}: HTTP {StatusCode} ({StatusName}), has value: {HasValue}", code, (int)metadataStatusCodeBySearch, metadataStatusCodeBySearch, video.HasValue);
            }

            if (!video.HasValue)
            {
                this.logger.LogInformation("[JellyfinJav] R18 - Metadata lookup returned null result.");
                return new MetadataResult<Movie>();
            }

            this.logger.LogInformation("[JellyfinJav] R18 - Found metadata: " + video);

            return new MetadataResult<Movie>
            {
                Item = new Movie
                {
                    OriginalTitle = info.Name,
                    Name = Utility.CreateVideoDisplayName(video.Value),
                    OfficialRating = "XXX",
                    PremiereDate = video.Value.ReleaseDate,
                    ProviderIds = new Dictionary<string, string> { { "R18", video.Value.Id } },
                    Studios = new[] { video.Value.Studio },
                    Genres = video.Value.Genres.ToArray(),
                },
                People = AddActressesToPeople(video.Value),
                HasMetadata = true,
            };
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo info, CancellationToken cancelToken)
        {
            if (info.ProviderIds.TryGetValue("R18", out var providerId) && !string.IsNullOrWhiteSpace(providerId))
            {
                this.logger.LogInformation("[JellyfinJav] R18 - Identify search using provider id: {R18Id}", providerId);
                var (videoById, identifyStatusCodeById) = await R18Client.LoadVideoWithStatus(providerId).ConfigureAwait(false);
                this.logger.LogInformation("[JellyfinJav] R18 - Identify search response for provider id {R18Id}: HTTP {StatusCode} ({StatusName}), has value: {HasValue}", providerId, (int)identifyStatusCodeById, identifyStatusCodeById, videoById.HasValue);
                if (!videoById.HasValue)
                {
                    this.logger.LogInformation("[JellyfinJav] R18 - Identify search result is null for provider id: {R18Id}", providerId);
                    return Array.Empty<RemoteSearchResult>();
                }

                this.logger.LogInformation("[JellyfinJav] R18 - Identify search found result for provider id: {R18Id}", providerId);
                return new[]
                {
                    new RemoteSearchResult
                    {
                        Name = !string.IsNullOrWhiteSpace(videoById.Value.Code) ? videoById.Value.Code : providerId,
                        ProviderIds = new Dictionary<string, string>
                        {
                            { "R18", videoById.Value.Id },
                        },
                        ImageUrl = videoById.Value.Cover,
                    },
                };
            }

            if (string.IsNullOrWhiteSpace(info.Name))
            {
                this.logger.LogInformation("[JellyfinJav] R18 - Search skipped: info.Name is null/empty and no provider id was supplied.");
                return Array.Empty<RemoteSearchResult>();
            }

            var javCode = Utility.ExtractCodeFromFilename(info.Name);
            if (string.IsNullOrEmpty(javCode))
            {
                this.logger.LogInformation("[JellyfinJav] R18 - Search skipped: unable to extract code from name '{Name}'.", info.Name);
                return Array.Empty<RemoteSearchResult>();
            }

            this.logger.LogInformation("[JellyfinJav] R18 - Getting Code: " + javCode);

            var (searchResults, searchStatusCode) = await R18Client.SearchWithStatus(javCode).ConfigureAwait(false);
            this.logger.LogInformation("[JellyfinJav] R18 - Search response for code {Code}: HTTP {StatusCode} ({StatusName})", javCode, (int)searchStatusCode, searchStatusCode);

            if (searchResults == null || !searchResults.Any())
            {
                this.logger.LogInformation("[JellyfinJav] R18 - No results found for code: " + javCode);
                return Array.Empty<RemoteSearchResult>(); // Return an empty collection, not null
            }

            var mapped = searchResults
                .Where(video => !string.IsNullOrWhiteSpace(video.Id))
                .Select(video => new RemoteSearchResult
                {
                    Name = !string.IsNullOrWhiteSpace(video.Code) ? video.Code : video.Id,
                    ProviderIds = new Dictionary<string, string>
                    {
                        { "R18", video.Id },
                    },
                    ImageUrl = video.Cover?.ToString(),
                })
                .ToList();

            if (!mapped.Any())
            {
                this.logger.LogInformation("[JellyfinJav] R18 - Search returned entries but all were filtered out due to missing ids for code: {Code}", javCode);
            }
            else
            {
                this.logger.LogInformation("[JellyfinJav] R18 - Returning {Count} search result(s) for code: {Code}", mapped.Count, javCode);
            }

            return mapped;
        }

        /// <inheritdoc />
        public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancelToken)
        {
            return await HttpClient.GetAsync(url, cancelToken).ConfigureAwait(false);
        }

        private static string NormalizeActressName(string name)
        {
            if (Plugin.Instance?.Configuration.ActressNameOrder == ActressNameOrder.LastFirst)
            {
                return string.Join(" ", name.Split(' ').Reverse());
            }

            return name;
        }

        private List<PersonInfo> AddActressesToPeople(Api.Video video)
        {
            var people = new List<PersonInfo>();

            if (video.Actresses == null || !video.Actresses.Any())
            {
                this.logger.LogInformation("[JellyfinJav] R18 - No actresses found in video metadata.");
                return people;
            }

            foreach (var actress in video.Actresses)
            {
                if (string.IsNullOrWhiteSpace(actress))
                {
                    continue;
                }

                var person = new PersonInfo
                {
                    Name = NormalizeActressName(actress),
                    Type = PersonKind.Actor
                };
                AddPerson(person, people);
            }

            return people;
        }

        private void AddPerson(PersonInfo p, List<PersonInfo> people)
        {
            if (people == null)
            {
                people = new List<PersonInfo>();
            }

            PeopleHelper.AddPerson(people, p);
        }
    }
}