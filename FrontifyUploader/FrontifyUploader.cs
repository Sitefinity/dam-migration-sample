using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Web;
using Newtonsoft.Json.Linq;
using Telerik.Sitefinity.Libraries.DamMigration;
using Telerik.Sitefinity.Libraries.DamMigration.Models;
using Telerik.Sitefinity.Libraries.Model;
using Telerik.Sitefinity.Localization;
using Telerik.Sitefinity.Modules.Libraries;

namespace SitefinityWebApp.Tests.DamMigration
{
    public class FrontifyUploader : IDamUploader
    {
        private string[] directories;
        private string token;
        private string projectId;
        private string frontifyApiUrl;

        public FrontifyUploader()
        {
        }

        public FrontifyUploader(string[] foldersPath, string token, string projectId, string frontifyApiUrl = "https://partners.frontify.com/graphql")
        {
            this.directories = foldersPath;
            this.token = token;
            this.projectId = projectId;
            this.frontifyApiUrl = frontifyApiUrl;
        }

        public void Init(Dictionary<string, object> parameters)
        {
            this.directories = ((IEnumerable)parameters[DirectoriesParameterKey]).Cast<string>().ToArray();
            this.token = parameters[TokenParameterKey]?.ToString();
            this.projectId = parameters[ProjectIdParameterKey]?.ToString();
            this.frontifyApiUrl = parameters[FrontifyUrlParameterKey]?.ToString();
        }

        public Dictionary<string, object> GetUploaderParams()
        {
            Dictionary<string, object> result = new Dictionary<string, object>();

            result.Add(DirectoriesParameterKey, directories);
            result.Add(TokenParameterKey, token);
            result.Add(ProjectIdParameterKey, projectId);
            result.Add(FrontifyUrlParameterKey, frontifyApiUrl);

            return result;
        }

        public DamMedia Upload(Guid mediaContentId, CultureInfo info, string provider)
        {
            using (var cultureRegion = new CultureRegion(info))
            {
                var librariesManager = LibrariesManager.GetManager(provider);
                var mediaContent = librariesManager.GetMediaItem(mediaContentId);

                // Frontify: Chunk size in bytes. Must be between 5MB and 1GB.
                int chunkSizeToUpload = 104857600; // 100 mb
                var fileDataChunks = this.DownloadMediaFile(librariesManager, mediaContent, chunkSizeToUpload);
                var fileName = mediaContent.FilePath.Split('/').LastOrDefault();

                // 1.Initialize the Upload
                var uploadFileResult = this.UploadFile(fileName, mediaContent.TotalSize, chunkSizeToUpload);
                if (!uploadFileResult.ErrorMessage.IsNullOrWhitespace())
                {
                    return new DamMedia() { ErrorMessage = uploadFileResult.ErrorMessage };
                }

                this.WaitFrontifyToProcessRequest();

                // 2.Upload binary file content
                this.UploadFileChunks(fileName, uploadFileResult.ChunksUrls, fileDataChunks);

                this.WaitFrontifyToProcessRequest();

                // 3.Use the file
                var createAssetResult = this.CreateAsset(uploadFileResult.FileId, mediaContent);
                if (!createAssetResult.ErrorMessage.IsNullOrWhitespace())
                {
                    return new DamMedia() { ErrorMessage = createAssetResult.ErrorMessage };
                }

                this.WaitFrontifyToProcessRequest();

                var getAssetByIdResult = this.GetAssetById(createAssetResult.AssetId, mediaContent, fileDataChunks.Count);
                if (!getAssetByIdResult.ErrorMessage.IsNullOrWhitespace())
                {
                    return new DamMedia() { AssetId = createAssetResult.AssetId, ErrorMessage = getAssetByIdResult.ErrorMessage };
                }

                return this.CreateDamAsset(createAssetResult.AssetId, getAssetByIdResult.Type, getAssetByIdResult.Url, getAssetByIdResult.DownloadUrl);
            }
        }

        public bool DeleteMedia(string mediaId, out string errorMessage)
        {
            errorMessage = string.Empty;
            var deleteResult = this.ExecuteQuery(this.GetDeleteByIdQuery(mediaId));

            if (deleteResult.Error != null)
            {
                errorMessage = "Error message while trying to delete asset: {0}".Arrange(deleteResult.Error);
                return false;
            }

            return true;
        }

        private DamMedia CreateDamAsset(string assetId, string type, string url, string downloadUrl)
        {
            var damAsset = new DamMedia() { AssetId = assetId };

            if (type == "Video")
            {
                damAsset.Extension = ".mp4";

                var videoFormatQueryParam = "format";
                if (url.IndexOf(videoFormatQueryParam) == -1)
                {
                    var appendSymbol = url.IndexOf("?") == -1 ? "?" : "&";
                    url += $"{appendSymbol}{videoFormatQueryParam}=.mp4";
                }
            }

            if (type != "Video" && type != "Image")
            {
                url = downloadUrl;
            }

            damAsset.Url = url;

            return damAsset;
        }

        private List<byte[]> DownloadMediaFile(LibrariesManager librariesManager, MediaContent mediaContent, int chunkSize)
        {
            var chunksFileData = new List<byte[]>();
            byte[] content = null;

            using (Stream stream = librariesManager.Download(mediaContent))
            {
                using (MemoryStream reader = new MemoryStream())
                {
                    stream.CopyTo(reader);
                    content = reader.ToArray();
                }
            }

            var totalChunks = content.LongLength / chunkSize;
            if (content.Length % chunkSize > 0)
                totalChunks++;

            for (int i = 0; i < totalChunks; i++)
            {
                chunksFileData.Add(content.Skip(i * chunkSize).Take(chunkSize).ToArray());
            }

            return chunksFileData;
        }

        private UploadFileResult UploadFile(string fileName, long fileSize, int chunkSize)
        {
            UploadFileResult result = new UploadFileResult();

            var uploadFileQuery = this.GetUploadFileQuery(fileName, fileSize, chunkSize);
            var fileUploadResult = this.ExecuteQuery(uploadFileQuery);

            if (string.IsNullOrEmpty(fileUploadResult.Error))
            {
                JToken uploadFileToken = this.GetToken(fileUploadResult.Response, "data.uploadFile");
                if (uploadFileToken != null)
                {
                    result.FileId = this.GetTokenValue<string>(uploadFileToken, "id");
                    result.ChunksUrls = this.GetTokenValue<JArray>(uploadFileToken, "urls").Select(p => p.Value<string>()).ToArray();
                    if (string.IsNullOrWhiteSpace(result.FileId) || result.ChunksUrls == null || result.ChunksUrls.Length == 0)
                    {
                        result.ErrorMessage = "Missing property values after UploadFile mutation.";
                    }
                }
                else
                {
                    result.ErrorMessage = "Upload file token is missing.";
                }
            }
            else
            {
                result.ErrorMessage = "Error message after executing mutation UploadFile query: {0}".Arrange(fileUploadResult.Error);
            }

            return result;
        }

        private CreateAssetResult CreateAsset(string fileId, MediaContent mediaContent)
        {
            CreateAssetResult result = new CreateAssetResult();

            string createAssetQuery = this.GetCreateAssetQuery(fileId, mediaContent);
            var createAssetResult = this.ExecuteQuery(createAssetQuery);

            if (string.IsNullOrEmpty(createAssetResult.Error))
            {
                result.AssetId = this.GetTokenValue<string>(createAssetResult.Response, "data.createAsset.job.assetId");
                if (string.IsNullOrEmpty(result.AssetId))
                {
                    result.ErrorMessage = "Asset Id cannot be obtained from CreateAsset query result";
                }
            }
            else
            {
                result.ErrorMessage = "Error message after executing CreateAsset query: {0}".Arrange(createAssetResult.Error);
            }

            return result;
        }

        private GetAssetResult GetAssetById(string assetId, MediaContent mediaContent, int chunksCount)
        {
            GetAssetResult result = new GetAssetResult();

            /// File is being processed by Frontify. We need to wait for the processing to complete in order to get the created dasset.
            /// As files are uploaed on chunks which are then processed fo creating the asset we are waiting at most 5 minutes per chunk
            /// if asset is not ready until then we are considering that its upload have failed
            DateTime waitUntil = DateTime.UtcNow.AddMinutes(chunksCount * 5);
            while (DateTime.UtcNow < waitUntil)
            {
                string getAssetByIdQuery = this.GetAssetByIdQuery(assetId, mediaContent);
                FrontifyRequestResponse getAssetResult = this.ExecuteQuery(getAssetByIdQuery);

                if (getAssetResult.Error == null)
                {
                    result.Url = this.GetTokenValue<string>(getAssetResult.Response, "data.asset.previewUrl");
                    result.Type = this.GetTokenValue<string>(getAssetResult.Response, "data.asset.type");
                    result.DownloadUrl = this.GetTokenValue<string>(getAssetResult.Response, "data.asset.downloadUrl");

                    if (result.Type != "Image" && result.Type != "Video" && result.DownloadUrl == null)
                    {
                        /// We are waiting 10 more seconds before we try to get the asset again
                        this.WaitFrontifyToProcessRequest(10);
                        continue;
                    }

                    break;
                }
                else
                {
                    result.ErrorMessage = "Error message after executing AssetById query: {0}".Arrange(getAssetResult.Error);
                    break;
                }
            }

            if (result.Type == null || result.Url == null || result.DownloadUrl == null)
            {
                result.ErrorMessage = "Missing property values after query by asset id.";
            }

            return result;
        }

        private List<string> GetFilePath(MediaContent mediaContent)
        {
            var segments = mediaContent.FilePath.Split('/');
            var filePath = segments.Skip(2).Take(segments.Length - 3).ToList();

            if (this.directories != null)
            {
                filePath.InsertRange(0, this.directories);
            }

            return filePath;
        }

        private FrontifyRequestResponse ExecuteQuery(string query)
        {
            FrontifyRequestResponse result = new FrontifyRequestResponse();

            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + this.token);
                httpClient.DefaultRequestHeaders.Add("x-frontify-beta", "enabled");
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var httpContent = new StringContent(query, Encoding.UTF8, "application/json");

                var httpResponseMessage = httpClient.PostAsync(this.frontifyApiUrl, httpContent).Result;
                var responseResult = httpResponseMessage.Content.ReadAsStringAsync().Result;
                result.Response = JValue.Parse(responseResult);
                if (result.Response == null)
                {
                    result.Error = "Error executing query: {0}".Arrange(query);
                }
                else
                {
                    var errors = this.GetTokenValue<JArray>(result.Response, "errors");
                    if (errors != null)
                    {
                        foreach (var error in errors)
                        {
                            result.Error += error["message"].ToString();
                        }
                    }
                }
            }

            return result;
        }

        private void UploadFileChunks(string fileName, string[] urls, List<byte[]> fileDataChunks)
        {
            string mimeMapping = MimeMapping.GetMimeMapping(fileName);
            for (int i = 0; i < urls.Length; i++)
            {
                this.UploadChunk(urls[i], mimeMapping, fileDataChunks[i]);
            }
        }

        private bool UploadChunk(string createdFrontifyFilePath, string mimeMapping, byte[] fileData)
        {
            using (var httpClient = new HttpClient())
            {
                var byteArrayContent = new ByteArrayContent(fileData);
                byteArrayContent.Headers.ContentType = new MediaTypeHeaderValue(mimeMapping);

                var response = httpClient.PutAsync(createdFrontifyFilePath, byteArrayContent).Result;
                return response.IsSuccessStatusCode;
            }
        }

        private string GetUploadFileQuery(string fileName, long fileSize, int chunkSize)
        {
            return "{{\"query\":\"mutation UploadFile($file: UploadFileInput!) {{ uploadFile(input: $file) {{ id urls }}}}\", \"variables\": {{ \"file\": {{ \"filename\": \"{0}\", \"size\": {1}, \"chunkSize\": {2} }} }} }}".Arrange(fileName, fileSize, chunkSize);
        }

        private string GetCreateAssetQuery(string fileId, MediaContent mediaContent)
        {
            var name = mediaContent.Title;
            var description = mediaContent.Description;
            var author = mediaContent.Author;
            var dirPath = string.Join(",", this.GetFilePath(mediaContent).Select(text => string.Format("\"" + text + "\"")));
            return "{{\"query\":\"mutation CreateAsset($asset: CreateAssetInput!) {{ createAsset(input: $asset) {{ job {{ assetId }} }} }}\", \"variables\": {{ \"asset\": {{ \"projectId\": \"{0}\", \"fileId\":  \"{1}\", \"title\": \"{2}\", \"description\": \"{3}\", \"directory\": [{4}], \"author\": \"{5}\" }} }} }}".Arrange(this.projectId, fileId, name, description, dirPath, author);
        }

        private string GetAssetByIdQuery(string assetId, MediaContent mediaContent)
        {
            // We can use bellow query part for ignoring retry logic on query, but if we use it, its throw exception when we are committing transaction
            string fields = "title description filename extension size previewUrl downloadUrl(permanent: true)";
            string select = "... on Document {{ {0} }} ... on Audio {{ {0} }} ... on File {{ {0} }}".Arrange(fields);
            if (mediaContent is Image)
            {
                select = "... on Image { title description filename extension size previewUrl downloadUrl }";
            }
            else if (mediaContent is Video)
            {
                select = "... on Video { title description filename extension size previewUrl downloadUrl }";
            }

            string result = "{{\"query\":\"query AssetById {{ asset: node(id: \\\"{0}\\\") {{ type: __typename id {1} }} }}\" }}".Arrange(assetId, select);
            return result;
        }

        private string GetDeleteByIdQuery(string assetId)
        {
            return "{{\"query\":\"mutation DeleteAsset($asset: DeleteAssetInput!) {{ deleteAsset(input: $asset) {{ asset {{ id }} }} }}\", \"variables\": {{ \"asset\": {{ \"id\": \"{0}\" }} }} }}".Arrange(assetId);
        }

        private JToken GetToken(JToken token, string path)
        {
            return token?.SelectToken(path);
        }

        private T GetTokenValue<T>(JToken token, string path)
        {
            JToken result = this.GetToken(token, path);
            if (result != null)
            {
                return result.Value<T>();
            }

            return default(T);
        }

        private void WaitFrontifyToProcessRequest(int waitDelayInSeconds = 1)
        {
            Thread.Sleep(1000 * waitDelayInSeconds);
        }

        private const string DirectoriesParameterKey = "FoldersPath";
        private const string TokenParameterKey = "Token";
        private const string ProjectIdParameterKey = "ProjectId";
        private const string FrontifyUrlParameterKey = "FrontifyUrl";
    }

    public class FrontifyRequestResponse
    {
        public JToken Response { get; set; }

        public string Error { get; set; }
    }

    public class UploadFileResult
    {
        public string FileId { get; set; }

        public string[] ChunksUrls { get; set; }

        public string ErrorMessage { get; set; }
    }

    public class CreateAssetResult
    {
        public string AssetId { get; set; }

        public string ErrorMessage { get; set; }
    }

    public class GetAssetResult
    {
        public string Type { get; set; }

        public string Url { get; set; }

        public string DownloadUrl { get; set; }

        public string ErrorMessage { get; set; }
    }
}