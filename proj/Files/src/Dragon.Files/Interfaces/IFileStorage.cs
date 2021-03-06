﻿using System;
using System.IO;

namespace Dragon.Files.Interfaces
{
    public interface IFileStorage
    {
        /// <summary>
        ///     Uploads a local file to a storage provider.
        /// </summary>
        /// <param name="filePath">The path of the file that is to be uploaded.</param>
        /// <param name="contentType">The content type of the file that is to be uploaded.</param>
        /// <returns>A unique id of the stored resource.</returns>
        String Store(String filePath, String contentType);

        /// <summary>
        ///     Uploads the content of a stream to a storage provider.
        /// </summary>
        /// <param name="content">The content of the file to store.</param>
        /// <param name="filePath">The path of the file. This is just used to check restrictions.</param>
        /// <param name="contentType">The content type of the file that is to be uploaded.</param>
        /// <returns>A unique id of the stored resource.</returns>
        String Store(Stream content, String filePath, String contentType);

        /// <summary>
        ///     Retrieves a file from a storage provider.
        /// </summary>
        /// <param name="resourceID">The unique id of the resource to retrieve.</param>
        /// <returns>The resource.</returns>
        Stream Retrieve(string resourceID);

        /// <summary>
        ///     Deletes a file from the storage provider.
        /// </summary>
        /// <param name="resourceID">The unique id of the resource to delete.</param>
        void Delete(string resourceID);

        /// <summary>
        ///     Checks if a file is available on the storage provider.
        /// </summary>
        /// <param name="resourceID">The unique id of the resource to search for.</param>
        /// <returns></returns>
        bool Exists(string resourceID);

        /// <summary>
        ///     Returns an Url that points to the resource. The Url typically is valid within the current hour, which allows caching.
        /// </summary>
        /// <param name="resourceID">The unique id of the resource to search for.</param>
        /// <returns></returns>
        string RetrieveAsUrl(string resourceID);
    }
}