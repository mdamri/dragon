﻿using System;
using System.IO;
using System.Text;
using Dragon.Files.Exceptions;
using Dragon.Files.Interfaces;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dragon.Files.Test
{
    /// <summary>
    /// Needs valid configuration provided in the application configuration file.
    /// See concrete test classes for details.
    /// Note: The tests will upload/download/remove test data to the storage provider!
    /// </summary>
    [TestClass]
    public abstract class FileStorageIntegrationTestBase
    {
        public abstract IFileStorage CreateFileStorage();

        protected const string TestFilePath = "resources/test.txt";
        protected const string DisallowedTestFilePath = "resources/test.php";
        protected const string TestFileContent = "hello s3!\r\n...\r\n..\r\n.\r\n";

        [TestMethod]
        [TestCategory("IntegrationTest")]
        [ExpectedException(typeof(ResourceToRetrieveNotFoundException))]
        public void Delete_inexistentFile_shouldThrowException()
        {
            var fileStorage = CreateFileStorage();
            fileStorage.Delete(Guid.NewGuid().ToString());
        }

        [TestMethod]
        [TestCategory("IntegrationTest")]
        public void Delete_validFile_shouldDeleteFile()
        {
            var fileStorage = CreateFileStorage();
            var id = fileStorage.Store(TestFilePath, null);
            Assert.AreNotEqual("", id);
            fileStorage.Delete(id);
            Assert.AreEqual(false, fileStorage.Exists(id));
        }

        [TestMethod]
        [TestCategory("IntegrationTest")]
        public void Exists_inexistentFile_shouldReturnFalse()
        {
            var fileStorage = CreateFileStorage();
            var actual = fileStorage.Exists(Guid.NewGuid().ToString());
            Assert.AreEqual(false, actual);
        }

        [TestMethod]
        [TestCategory("IntegrationTest")]
        public void Exists_validFile_shouldReturnTrue()
        {
            var fileStorage = CreateFileStorage();
            var id = fileStorage.Store(TestFilePath, null);
            var actual = fileStorage.Exists(id);
            fileStorage.Delete(id); // cleanup
            Assert.AreEqual(true, actual);
        }

        [TestMethod]
        [TestCategory("IntegrationTest")]
        public void Store_validFile_shouldUploadFile()
        {
            var fileStorage = CreateFileStorage();
            var id = fileStorage.Store(TestFilePath, null);
            Assert.IsFalse(string.IsNullOrEmpty(id));
            var exists = fileStorage.Exists(id);
            fileStorage.Delete(id); // cleanup
            Assert.AreEqual(true, exists);
        }

        [TestMethod]
        [TestCategory("IntegrationTest")]
        [ExpectedException(typeof(ResourceToStoreNotFoundException))]
        public void Store_invalidFile_shouldThrowException()
        {
            var fileStorage = CreateFileStorage();
            fileStorage.Store(TestFilePath + "doesnotexist", null);
        }

        [TestMethod]
        [TestCategory("IntegrationTest")]
        [ExpectedException(typeof(FileTypeNotAllowedException))]
        public void Store_disallowedFile_shouldThrowException()
        {
            var fileStorage = CreateFileStorage();
            fileStorage.Store(DisallowedTestFilePath, null);
        }

        [TestMethod]
        [TestCategory("IntegrationTest")]
        [ExpectedException(typeof(ResourceToStoreNotFoundException))]
        public void Store_nullStream_shouldThrowException()
        {
            var fileStorage = CreateFileStorage();
            fileStorage.Store(null, "blah.txt");
        }

        [TestMethod]
        [TestCategory("IntegrationTest")]
        public void Store_validStream_shouldUploadFile()
        {
            const string content = "testcontent\n\n23";
            var fileStorage = CreateFileStorage();
            var id = fileStorage.Store(new MemoryStream(Encoding.UTF8.GetBytes(content)), "blah.txt", null);
            Assert.IsFalse(string.IsNullOrEmpty(id));
            var exists = fileStorage.Exists(id);
            var actual = new StreamReader(fileStorage.Retrieve(id)).ReadToEnd();
            fileStorage.Delete(id); // cleanup
            Assert.AreEqual(true, exists);
            Assert.AreEqual(content, actual);
        }

        [TestMethod]
        [TestCategory("IntegrationTest")]
        [ExpectedException(typeof(FileTypeNotAllowedException))]
        public void Store_disallowedStream_shouldThrowException()
        {
            var fileStorage = CreateFileStorage();
            fileStorage.Store(new MemoryStream(Encoding.UTF8.GetBytes("blah")), DisallowedTestFilePath, null);
        }

        [TestMethod]
        [TestCategory("IntegrationTest")]
        [ExpectedException(typeof(ResourceToRetrieveNotFoundException))]
        public void Retrieve_invalidFile_shouldThrowException()
        {
            var fileStorage = CreateFileStorage();
            fileStorage.Retrieve(Guid.NewGuid().ToString());
        }

        [TestMethod]
        [TestCategory("IntegrationTest")]
        public void Retrieve_validFile_shouldDownloadFile()
        {
            var fileStorage = CreateFileStorage();
            var id = fileStorage.Store(TestFilePath, null);
            var actual = new StreamReader(fileStorage.Retrieve(id)).ReadToEnd();
            fileStorage.Delete(id); // cleanup
            Assert.AreEqual(TestFileContent, actual);
        }

    }
}
