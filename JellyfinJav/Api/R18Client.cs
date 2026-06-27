namespace JellyfinJav.Api
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading.Tasks;

    /// <summary>A client for looking up R18 metadata from the bundled dump.</summary>
    public static class R18Client
    {
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
            return await R18Database.SearchWithStatus(searchCode).ConfigureAwait(false);
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
            using var enumerator = results.GetEnumerator();
            if (!enumerator.MoveNext())
            {
                return (null, searchStatusCode);
            }

            return await LoadVideoWithStatus(enumerator.Current.Id).ConfigureAwait(false);
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
            return await R18Database.LoadVideoWithStatus(id).ConfigureAwait(false);
        }
    }
}