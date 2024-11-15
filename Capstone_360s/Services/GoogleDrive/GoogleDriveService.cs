﻿using Capstone_360s.Utilities;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Drive.v3;
using Google.Apis.Upload;
using Microsoft.Extensions.Caching.Memory;
using Capstone_360s.Interfaces.IService;

namespace Capstone_360s.Services.GoogleDrive
{
    public class GoogleDriveService : IGoogleDrive
    {
        private readonly DriveService _driveClient;
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<GoogleDriveService> _logger;

        public GoogleDriveService(DriveService driveClient,
            IMemoryCache memoryCache,
            ILogger<GoogleDriveService> logger)
        {
            _driveClient = driveClient;
            _memoryCache = memoryCache;
            _logger = logger;
        }

        public async Task<string> CreateFolderAsync(string folderName, string parentFolderId = null)
        {
            _logger.LogInformation($"Received request to create folder {folderName}...");

            if (string.IsNullOrEmpty(folderName))
            {
                throw new ArgumentNullException(nameof(folderName));
            }

            var fileMetadata = new Google.Apis.Drive.v3.Data.File()
            {
                Name = folderName,
                MimeType = "application/vnd.google-apps.folder"
            };

            if (!string.IsNullOrEmpty(parentFolderId))
            {
                fileMetadata.Parents = new List<string> { parentFolderId };
            }

            try
            {
                var request = _driveClient.Files.Create(fileMetadata);
                request.Fields = "id";
                var folder = await request.ExecuteAsync();

                await MakeFilePublicAsync(folder.Id);

                _logger.LogInformation($"Folder '{folderName}' created successfully with ID: {folder.Id}");
                return folder.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error creating folder '{folderName}': {ex.Message}");
                throw;
            }
        }


        public async Task<(Google.Apis.Drive.v3.Data.File, MemoryStream)> GetOneFile(string fileId, bool download = true)
        {
            if (string.IsNullOrEmpty(fileId))
            {
                throw new ArgumentNullException(nameof(fileId));
            }

            var cacheKey = $"GoogleDrive-{fileId}";

            if (!_memoryCache.TryGetValue(cacheKey, out (Google.Apis.Drive.v3.Data.File file, MemoryStream stream) cachedFile))
            {
                // Check if the cached stream is valid
                if (cachedFile.stream != null && cachedFile.stream.CanRead)
                {
                    return cachedFile;
                }
                else
                {
                    // Invalidate the cache entry if the stream is closed
                    _memoryCache.Remove(cacheKey);
                }

                var request = _driveClient.Files.Get(fileId);
                var stream = new MemoryStream();
                try
                {
                    var file = await request.ExecuteAsync();

                    if (download)
                    {
                        request.MediaDownloader.ProgressChanged += GoogleDriveUtility.Download_ProgressChanged;

                        await request.DownloadAsync(stream);
                    }

                    if (file == null)
                    {
                        _logger.LogInformation($"Did not find file with id '{fileId}'");
                        throw new FileNotFoundException("File not found.");
                    }

                    _logger.LogInformation($"File with id '{fileId}' found.");

                    stream.Position = 0;
                    cachedFile = (file, stream);

                    // Set cache options
                    var cacheEntryOptions = new MemoryCacheEntryOptions()
                        .SetSlidingExpiration(TimeSpan.FromHours(1)); // Adjust the expiration time as needed

                    // Save data in cache
                    _memoryCache.Set(cacheKey, cachedFile, cacheEntryOptions);
                }
                catch (Exception)
                {
                    throw;
                }
            }

            _logger.LogInformation($"{cacheKey} cached on server successfully.");
            return cachedFile;
        }

        public IList<string> GetFileIds(IList<Google.Apis.Drive.v3.Data.File> files)
        {
            var idList = new List<string>();

            if (files == null || files.Count == 0)
            {
                _logger.LogInformation($"{nameof(files)} was empty.");
                return idList;
            }
            else
            {
                _logger.LogInformation($"Profile picture ids parsed.");
                foreach (var file in files)
                {
                    idList.Add(file.Id);
                }
                return idList;
            }
        }

        public async Task<IList<Google.Apis.Drive.v3.Data.File>> QueryForAllFiles(string folderId, string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                throw new ArgumentNullException(nameof(query));
            }

            var request = _driveClient.Files.List();
            request.Q = $"name contains '{query}' and '{folderId}' in parents and trashed = false";
            request.Fields = "files(id, name, mimeType, parents)";

            var result = await request.ExecuteAsync();

            if (result.Files == null || result.Files.Count == 0)
            {
                _logger.LogInformation($"Did not find query '{query}'");
                return new List<Google.Apis.Drive.v3.Data.File>();
            }
            else
            {
                _logger.LogInformation($"Found query '{query}'");
                return result.Files;
            }
        }

        public async Task<IList<string>> GetAllFileIdsFromQuery(string folderId, string query)
        {
            var files = await QueryForAllFiles(folderId, query);
            return GetFileIds(files);
        }

        private async Task MakeFilePublicAsync(string fileId)
        {
            if (string.IsNullOrEmpty(fileId))
            {
                throw new ArgumentException("File ID is required.");
            }

            var permission = new Permission
            {
                Role = "reader",
                Type = "anyone"
            };

            try
            {
                await _driveClient.Permissions.Create(permission, fileId).ExecuteAsync();
            }
            catch (Exception ex)
            {
                throw new Exception("An error occurred while making the file public: " + ex.Message);
            }
        }

        public async Task<string> UploadFile(IFormFile file, string folderId)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            var fileMetaData = new Google.Apis.Drive.v3.Data.File()
            {
                Name = DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss") + "_" + file.FileName,
                Parents = new List<string> { folderId },
                MimeType = file.ContentType
            };

            using var fileStream = file.OpenReadStream();

            FilesResource.CreateMediaUpload request = _driveClient.Files.Create(fileMetaData, fileStream, fileMetaData.MimeType);

            request.ProgressChanged += GoogleDriveUtility.Upload_ProgressChanged;

            var result = await request.UploadAsync();

            if (result.Status != UploadStatus.Completed)
            {
                throw new Exception("Upload to Google Drive 'Profile Picture' folder failed.");
            }
            else
            {
                var fileId = request.ResponseBody.Id;
                _logger.LogInformation("Upload to Google Drive 'Profile Picture' folder succeeded.");

                await MakeFilePublicAsync(fileId);

                return fileId;
            }
        }

        // upload file as byte array
        public async Task<string> UploadFile(byte[] file, string fileName, string folderId)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            var fileMetaData = new Google.Apis.Drive.v3.Data.File()
            {
                Name = fileName,
                Parents = new List<string> { folderId },
                MimeType = "application/octet-stream"
            };

            using var fileStream = new MemoryStream(file);

            FilesResource.CreateMediaUpload request = _driveClient.Files.Create(fileMetaData, fileStream, fileMetaData.MimeType);

            request.ProgressChanged += GoogleDriveUtility.Upload_ProgressChanged;

            var result = await request.UploadAsync();

            if (result.Status != UploadStatus.Completed)
            {
                throw new Exception("Upload to Google Drive folder failed.");
            }
            else
            {
                var fileId = request.ResponseBody.Id;
                _logger.LogInformation("Upload to Google Drive succeeded.");

                await MakeFilePublicAsync(fileId);

                return fileId;
            }
        }

        public async Task DeleteFile(string fileId)
        {
            if (string.IsNullOrEmpty(fileId))
            {
                throw new ArgumentNullException(nameof(fileId));
            }

            try
            {
                var request = _driveClient.Files.Delete(fileId);
                await request.ExecuteAsync();
                _logger.LogInformation($"File with id {fileId} was deleted successfully.");
            }
            catch (Exception)
            {
                throw;
            }
        }
    }
}

