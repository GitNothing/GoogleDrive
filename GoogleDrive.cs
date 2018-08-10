using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System.IO;
using System.Linq;
using System.Threading;
using Google.Apis.Drive.v3.Data;
using File = Google.Apis.Drive.v3.Data.File;
using MimeTypes;

namespace GoogleDrive
{
    //Need this in the application. Install-Package Google.Apis.Drive.v3
    public class GoogleDrive
    {
        public Action<float> ProgressCallbackCreate { get; set; }
        public Action<float> ProgressCallbackUpdate { get; set; }
        readonly string _applicationName;
        private readonly string _clientIdFilePath;
        private static DriveService _service;
        private static bool _isAuthenticated;
        private readonly int AuthenticateTimeout = 30000;

        private GoogleDrive(string applicationName, string clientIdFilePath)
        {
            _applicationName = applicationName;
            _clientIdFilePath = clientIdFilePath;
        }
        public static async Task<GoogleDrive> Factory(string applicationName, string clientIdFilePath)
        {
            var instance = new GoogleDrive(applicationName, clientIdFilePath);
            await instance.Autenticate();
            return instance;
        }

        public async Task Reauthenticate()
        {
            string credPath = System.Environment.GetFolderPath(
                Environment.SpecialFolder.Personal);
            credPath = Path.Combine(credPath, ".credentials");
            if(Directory.Exists(credPath))
                Directory.Delete(credPath, true);
            await Autenticate();
        }
        private async Task Autenticate()
        {
            UserCredential credential;
            string[] scopes = { DriveService.Scope.Drive };
            var task = Task.Run(() =>
            {
                using (var stream =
                    new FileStream(_clientIdFilePath, FileMode.Open, FileAccess.Read))
                {
                    string credPath = System.Environment.GetFolderPath(
                        Environment.SpecialFolder.Personal);
                    credPath = Path.Combine(credPath, ".credentials/GoogleDriveAuthCache.json");

                    credential = GoogleWebAuthorizationBroker.AuthorizeAsync(
                        GoogleClientSecrets.Load(stream).Secrets,
                        scopes,
                        "user",
                        CancellationToken.None,
                        new FileDataStore(credPath, true)).Result;
                    return credential;
                }
            });

            if (await Task.WhenAny(task, Task.Delay(AuthenticateTimeout)) == task)
            {
                var cred = task.Result;
                _service = new DriveService(new BaseClientService.Initializer()
                {
                    HttpClientInitializer = cred,
                    ApplicationName = _applicationName,
                });
                _isAuthenticated = true;
            }
            else
            {
                Debug.Fail("Credential timeout");
            }
        }

        public async Task<bool> DeleteFile(string id)
        {
            try
            {
                await _service.Files.Delete(id).ExecuteAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<string> UpdateFile(string filePath, string fileId)
        {
            long size = 0;
            if (!System.IO.File.Exists(filePath))
            {
                throw new Exception("CreateFile, File not found");
            }
            using (FileStream stream = new FileStream(filePath, FileMode.Open))
            {
                size = stream.Length;
                var name = Path.GetFileName(filePath);
                var mimeType = Path.GetExtension(name);
                mimeType = MimeTypeMap.GetMimeType(mimeType);
                var meta = new File();
                meta.Name = name;
                meta.MimeType = mimeType;

                var request = _service.Files.Update(meta, fileId, stream, mimeType);
                request.Fields = "id";
                request.ProgressChanged += (progress) =>
                {
                    var prog = (float)progress.BytesSent * 100 / size;
                    ProgressCallbackUpdate?.Invoke(prog);
                };
                await request.UploadAsync();
            }
            return fileId;
        }

        public async Task<string> CreateFile(string filePath, string folderId = null, bool isPublic = false)
        {
            var returnId = "";
            long size = 0;
            if (!System.IO.File.Exists(filePath))
            {
                throw new Exception("CreateFile, File not found");
            }
            using (FileStream stream = new FileStream(filePath, FileMode.Open))
            {
                size = stream.Length;
                var name = Path.GetFileName(filePath);
                var mimeType = Path.GetExtension(name);
                mimeType = MimeTypeMap.GetMimeType(mimeType);
                var meta = new File();
                meta.Name = name;
                meta.MimeType = mimeType;

                if (folderId != null)
                {
                    var hasFolder = await GetFileById(folderId);
                    if (hasFolder.isFolder)
                    {
                        meta.Parents = new List<string>() { folderId };
                    }
                    else
                    {
                        throw new Exception("No folder was found to put into");
                    }
                }

                var request = _service.Files.Create(
                    meta, stream, mimeType);
                request.Fields = "id";
                request.ProgressChanged += (progress) =>
                {
                    var prog = (float)progress.BytesSent*100 / size;
                    ProgressCallbackCreate?.Invoke(prog);
                };
                await request.UploadAsync();

                if (isPublic)
                {
                    await ShareLinkToggle(request.ResponseBody.Id, true);
                }
                returnId = request.ResponseBody.Id;
            }
            
            return returnId;
        }

        //enable or disable sharelink
        public async Task<GoogleDriveFileData> ShareLinkToggle(string fileId, bool isOn)
        {
            var p = FactoryPublicPermission();
            if (isOn)
            {
                var request = _service.Permissions.Create(p, fileId);
                await request.ExecuteAsync();
            }
            else
            {
                var request = _service.Permissions.Delete(fileId, "anyoneWithLink");
                await request.ExecuteAsync();
            }
            var find = GetFileById(fileId).Result.Name;
            var find2 = GetFiles(find, "name").Result;
            return find2.FirstOrDefault();
        }


        //returns new folder id
        public async Task<string> CreateFolder(string name, string folderId = null, bool isPublic = false)
        {
            var meta = new File()
            {
                Name = name,
                MimeType = "application/vnd.google-apps.folder"
            };

            if (folderId != null)
            {
                var hasFolder = await GetFileById(folderId);
                if (hasFolder.isFolder)
                {
                    meta.Parents = new List<string>() { folderId };
                }
                else
                {
                    throw new Exception("No folder was found to put into");
                }
            }
            
            var request = _service.Files.Create(meta);
            request.Fields = "id";
            var file = await request.ExecuteAsync();

            if (isPublic)
            {
                await ShareLinkToggle(file.Id, true);
            }
            return file.Id;
        }


        private Permission FactoryPublicPermission()
        {
            var permission = new Permission();
            permission.Role = "reader";
            permission.Type = "anyone";
            return permission;
        }

        public async Task<GoogleDriveFileData> GetFileById(string id)
        {
            try
            {
                File file = await _service.Files.Get(id).ExecuteAsync();
                return GetDataFile(file);
            }
            catch
            {
                return GetDataFile(null);
            }
        }

        public async Task DownloadFile(string fileID, string fullPathAndFileName)
        {
            var request = _service.Files.Get(fileID);
            var stream = new System.IO.MemoryStream();
            await request.DownloadAsync(stream);
            FileStream file = new FileStream(fullPathAndFileName, FileMode.Create, FileAccess.ReadWrite);
            stream.WriteTo(file);
            stream.Close();
            file.Close();
        }

        public async Task<bool> DeleteEmptyFolder(string folderID)
        {
            var files = await GetFilesInFolder(folderID);
            if (files.Count < 1)
            {
                await DeleteFile(folderID);
                return true;
            }
            return false;
        }
        public async Task<List<GoogleDriveFileData>> GetFilesInFolder(string folderID)
        {
            var test = await GetFileById(folderID);
            if (!test.isFolder)
            {
                throw new Exception("ID is not a folder type");
            }
            string pageToken = null;
            var returns = await Task.Run(() =>
            {
                var returnList = new List<GoogleDriveFileData>();
                do
                {
                    var fileMetadata = _service.Files.List();
                    fileMetadata.Fields = "nextPageToken, files(id, name, webViewLink, shared, parents, permissions, mimeType)";
                    fileMetadata.Spaces = "drive";
                    fileMetadata.Q = $"parents in '{folderID}'";
                    fileMetadata.PageToken = pageToken;
                    var result = fileMetadata.Execute();
                    foreach (var file in result.Files)
                    {
                        returnList.Add(GetDataFile(file));
                    }
                    pageToken = result.NextPageToken;

                } while (pageToken != null);
                return returnList;
            });
            return returns;
        }

        public async Task<List<GoogleDriveFileData>> GetFiles(string search, string field)
        {
            string pageToken = null;
            var returns = await Task.Run(() =>
            {
                var returnList = new List<GoogleDriveFileData>();
                do
                {
                    var fileMetadata = _service.Files.List();
                    fileMetadata.Fields = "nextPageToken, files(id, name, webViewLink, shared, parents, permissions, mimeType)";
                    fileMetadata.Spaces = "drive";
                    fileMetadata.Q = $"{field} contains '{search}'";
                    fileMetadata.PageToken = pageToken;
                    var result = fileMetadata.Execute();
                    foreach (var file in result.Files)
                    {
                        Console.WriteLine(String.Format(
                            "Found file: {0} ({1})", file.Name, file.Id));
                        returnList.Add(GetDataFile(file));
                    }
                    pageToken = result.NextPageToken;

                } while (pageToken != null);
                return returnList;
            });
            return returns;
        }

        private GoogleDriveFileData GetDataFile(File file)
        {
            var data = new GoogleDriveFileData();
            if (file == null) return data;
            if (file.MimeType == "application/vnd.google-apps.folder") data.isFolder = true;
            data.isFound = true;
            data.Id = file.Id;
            data.Name = file.Name;
            data.Link = file.WebViewLink;
            data.IsShared = file.Shared != null && file.Shared == true;
            if (file.Permissions != null)
            {
                data.Permissions = file.Permissions.ToList();
            }
            if (file.Parents != null)
            {
                data.Parents = file.Parents.ToList();
            }
            return data;
        }
    }
    
    public class GoogleDriveFileData
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public List<string> Parents { get; set; }
        public bool IsShared { get; set; }
        public string Link { get; set; }
        public List<Permission> Permissions { get; set; }
        public bool isFound { get; set; }
        public bool isFolder { get; set; }
    }
}
