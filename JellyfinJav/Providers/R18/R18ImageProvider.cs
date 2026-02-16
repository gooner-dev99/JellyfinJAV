namespace JellyfinJav.Providers.R18Provider
{
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using static System.Net.WebRequestMethods;

    /// <summary>The provider for R18 video covers.</summary>
    public class R18ImageProvider : IRemoteImageProvider, IHasOrder
    {
        private static readonly HttpClient HttpClient = new HttpClient();

        /// <summary>Initializes a new instance of the <see cref="R18ImageProvider"/> class.</summary>
        public R18ImageProvider()
        {
            HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        }

        /// <inheritdoc />
        public string Name => "R18";

        /// <inheritdoc />
        public int Order => 99;

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancelToken)
        {
            var id = item.GetProviderId("R18");
            if (string.IsNullOrEmpty(id))
            {
                return Array.Empty<RemoteImageInfo>();
            }

            var primaryImageFormats = new[]
            {
                $"https://awsimgsrc.dmm.com/dig/digital/video/{id}/{id}pl.jpg",
                $"https://pics.dmm.co.jp/mono/movie/adult/{id}/{id}pl.jpg",
                $"https://awsimgsrc.dmm.com/dig/mono/movie/{id}/{id}pl.jpg",
            };

            var primaryImage = await this.GetValidImageUrl(primaryImageFormats, cancelToken);

            if (string.IsNullOrEmpty(primaryImage))
            {
                // If no valid image URL is found, return the fallback URL
                primaryImage = $"https://awsimgsrc.dmm.com/dig/digital/video/{id}/{id}pl.jpg";
            }

            if (string.IsNullOrEmpty(primaryImage))
            {
                // If the primary image URL is empty, return an empty collection
                return Array.Empty<RemoteImageInfo>();
            }

            return new[]
            {
        new RemoteImageInfo
        {
            ProviderName = this.Name,
            Type = ImageType.Primary,
            Url = primaryImage,
        },
            };
        }

        /// <inheritdoc />
        public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancelToken)
        {
            return await HttpClient.GetAsync(url, cancelToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new[] { ImageType.Primary };
        }

        /// <inheritdoc />
        public bool Supports(BaseItem item) => item is Movie;

        private async Task<string?> GetValidImageUrl(IEnumerable<string> imageFormats, CancellationToken cancellationToken)
        {
            foreach (var imageUrl in imageFormats)
            {
                try
                {
                    using (var response = await HttpClient.GetAsync(imageUrl, cancellationToken).ConfigureAwait(false))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            // If the response is successful, return the URL
                            return imageUrl;
                        }
                    }
                }
                catch (HttpRequestException)
                {
                    // Ignore HttpRequestException and try the next URL
                }
                catch (OperationCanceledException)
                {
                    // If the operation is canceled, propagate the cancellation
                    throw;
                }
                catch (Exception)
                {
                    // Ignore other exceptions and try the next URL
                }
            }

            // If no valid image URL is found, return null
            return null;
        }
    }
}