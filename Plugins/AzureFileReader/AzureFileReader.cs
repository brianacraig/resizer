/* Copyright (c) 2011 Wouter A. Alberts and Nathanael D. Jones. See license.txt for your rights. */
using System;
using System.Collections.Specialized;
using System.Web;
using System.Web.Hosting;
using ImageResizer.Util;
using System.Collections.Generic;
using ImageResizer.Configuration.Issues;
using System.Security;
using ImageResizer.Configuration.Xml;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.File;
using ImageResizer.Storage;
using ImageResizer.ExtensionMethods;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Azure;
using System.Runtime.Caching;

namespace ImageResizer.Plugins.AzureFileReader {

    public class AzureFileReaderPlugin : BlobProviderBase, IMultiInstancePlugin {

        public CloudFileClient CloudFileClient { get; set; }
        string fileStorageConnection;
        string fileStorageEndpoint;
        int rootDirectoryCacheInMinutes;

        public AzureFileReaderPlugin()
            : base()
        {
            this.VirtualFilesystemPrefix = "~/azure";

        }
        public AzureFileReaderPlugin(NameValueCollection args):this() {
            LoadConfiguration(args);
            fileStorageConnection = args["connectionstring"];
            fileStorageEndpoint = args.GetAsString("filestorageendpoint", args.GetAsString("endpoint",null));
            string rootDirectCacheTime = args.GetAsString("rootDirectoryCacheInMinutes", "0");
            rootDirectoryCacheInMinutes = int.Parse(rootDirectCacheTime);
        }


        protected CloudFile GetFileRef(string virtualPath)
        {

            string subPath = StripPrefix(virtualPath).Trim('/', '\\');

            // first folder is the share, everything after is the directory and file.
            string shareName = subPath.Substring(0, subPath.IndexOf('/'));
            string fileName = subPath.Substring(shareName.Length+1);

            CloudFileDirectory rootDirectory = GetRootFromCache(shareName);
            return rootDirectory.GetFileReference(fileName);
        }

        private CloudFileDirectory GetRootFromCache(string shareName) {
            if (rootDirectoryCacheInMinutes <= 0) {
                return GetDirectoryRootFromAzure(shareName);
            }

            string cacheKey = $"RootDirectory_{shareName}";

            Lazy<CloudFileDirectory> lazyRoot = new Lazy<CloudFileDirectory>(() => GetDirectoryRootFromAzure(shareName));
            var item = (Lazy<CloudFileDirectory>)MemoryCache.Default.AddOrGetExisting(cacheKey, lazyRoot, System.DateTime.Now.AddMinutes(rootDirectoryCacheInMinutes)) ?? lazyRoot;
            
            // Double check that we're not caching an error result.
            try {
                if (item.Value == null) {
                    MemoryCache.Default.Remove(cacheKey);
                }
            } catch (Exception) {
                MemoryCache.Default.Remove(cacheKey);
            }

            return item.Value;
        }

        private CloudFileDirectory GetDirectoryRootFromAzure(string shareName) 
        {
            var shareReference = CloudFileClient.GetShareReference(shareName);
            return shareReference.GetRootDirectoryReference();
        }

        public override async Task<Stream> OpenAsync(string virtualPath, NameValueCollection queryString)
        {

            MemoryStream ms = new MemoryStream(4096); // 4kb is a good starting point.

            // Synchronously download
            try {
                var cloudFile = GetFileRef(virtualPath);
                await cloudFile.DownloadToStreamAsync(ms);
            }
            catch (StorageException e)
            {
                ms.Close();
                ms.Dispose();
                if (e.RequestInformation.HttpStatusCode == 404)
                {
                    throw new FileNotFoundException("Azure file not found", e);
                }
                throw;
            } 
            catch (Exception) 
            { 
                ms.Close();
                ms.Dispose();
            }

            ms.Seek(0, SeekOrigin.Begin); // Reset to beginning
            return ms;
        }

        public override Task<IBlobMetadata> FetchMetadataAsync(string virtualPath, NameValueCollection queryString) {
            throw new NotImplementedException();
        }

        public override IPlugin Install(Configuration.Config c) {
            if (string.IsNullOrEmpty(fileStorageConnection))
                throw new InvalidOperationException("AzureFileReader requires a named connection string or a connection string to be specified with the 'connectionString' attribute.");

            // Setup the connection to Windows Azure Storage
            //Lookup named connection string first, then fall back.
            var connectionString = CloudConfigurationManager.GetSetting(fileStorageConnection);
            if (string.IsNullOrEmpty(connectionString)) { connectionString = fileStorageConnection; }
            

            CloudStorageAccount cloudStorageAccount;
            if (CloudStorageAccount.TryParse(connectionString, out cloudStorageAccount)){
                if (string.IsNullOrEmpty(fileStorageEndpoint)){
                    fileStorageEndpoint = cloudStorageAccount.FileEndpoint.ToString();
                }
            }else{
                throw new InvalidOperationException("Invalid AzureFileReader connectionString value; rejected by Azure SDK.");
            }
            if (!fileStorageEndpoint.EndsWith("/"))
                fileStorageEndpoint += "/";

            this.CloudFileClient = cloudStorageAccount.CreateCloudFileClient();

            base.Install(c);

            return this;
        }

    }
}
