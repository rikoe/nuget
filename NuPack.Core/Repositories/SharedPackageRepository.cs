﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace NuGet {
    public class SharedPackageRepository : LocalPackageRepository, ISharedPackageRepository {
        private const string StoreFilePath = "repositories.config";

        public SharedPackageRepository(string path)
            : base(path) {
        }

        public SharedPackageRepository(IPackagePathResolver resolver, IFileSystem fileSystem)
            : base(resolver, fileSystem) {
        }

        public void RegisterRepository(string path) {
            AddEntry(path);
        }

        public void UnregisterRepository(string path) {
            DeleteEntry(path);
        }

        public bool IsReferenced(string packageId, Version version) {
            // See if this package exists in any other repository before we remove it
            return GetRepositories().Any(r => r.Exists(packageId, version));
        }

        protected virtual IPackageRepository CreateRepository(string path) {
            string directory = FileSystem.GetFullPath(Path.GetDirectoryName(path));
            return new PackageReferenceRepository(new PhysicalFileSystem(directory), this);
        }

        private IEnumerable<IPackageRepository> GetRepositories() {
            return GetRepositoryPaths().Select(CreateRepository);
        }

        private IEnumerable<string> GetRepositoryPaths() {
            // The store file is in this format
            // <repositories>
            //     <repository path="..\packages.config" />
            // </repositories>
            XDocument document = GetStoreDocument();

            // The document doesn't exist so do nothing
            if (document == null) {
                return Enumerable.Empty<string>();
            }

            // Paths have to be relative to the this repository
            var entries = from e in GetRepositoryElements(document)
                          select new {
                              Element = e,
                              Path = e.GetOptionalAttributeValue("path")
                          };

            var paths = new HashSet<string>();
            foreach (var entry in entries.ToList()) {
                string path = NormalizePath(entry.Path);

                if (String.IsNullOrEmpty(path) || 
                    !FileSystem.FileExists(path) || 
                    !paths.Add(path)) {
                    // Remove invalid entries from the document
                    entry.Element.Remove();
                }
            }

            SaveDocument(document);

            return paths;
        }

        private void AddEntry(string path) {
            path = NormalizePath(path);

            // Create the document if it doesn't exist
            XDocument document = GetStoreDocument(createIfNotExists: true);

            XElement element = FindEntry(document, path);

            if (element != null) {
                // The path exists already so do nothing
                return;
            }

            document.Root.Add(new XElement("repository",
                                  new XAttribute("path", path)));

            SaveDocument(document);
        }

        private void DeleteEntry(string path) {
            // Get the relative path
            path = NormalizePath(path);
 
            // Remove the repository from the document
            XDocument document = GetStoreDocument();

            if (document == null) {
                return;
            }

            XElement element = FindEntry(document, path);

            if (element != null) {
                element.Remove();
            }

            // REVIEW: Should we remove the file if no projects reference this repository?
            SaveDocument(document);
        }

        private static IEnumerable<XElement> GetRepositoryElements(XDocument document) {
            return from e in document.Root.Elements("repository")
                   select e;
        }

        private XElement FindEntry(XDocument document, string path) {
            path = NormalizePath(path);

            return (from e in GetRepositoryElements(document)
                    let entryPath = NormalizePath(e.GetOptionalAttributeValue("path"))
                    where path.Equals(entryPath, StringComparison.OrdinalIgnoreCase)
                    select e).FirstOrDefault();
        }

        private void SaveDocument(XDocument document) {
            ILogger logger = FileSystem.Logger;
            try {
                // Don't log anything when saving the xml file
                FileSystem.Logger = null;
                FileSystem.AddFile(StoreFilePath, document.Save);
            }
            finally {
                FileSystem.Logger = logger;
            }
        }

        private XDocument GetStoreDocument(bool createIfNotExists = false) {
            // If the file exists then open and return it
            if (FileSystem.FileExists(StoreFilePath)) {
                using (Stream stream = FileSystem.OpenFile(StoreFilePath)) {
                    return XDocument.Load(stream);
                }
            }

            // If it doesn't exist and we're creating a new file then return a
            // document with an empty packages node
            if (createIfNotExists) {
                return new XDocument(new XElement("repositories"));
            }

            return null;
        }

        private string NormalizePath(string path) {
            if (String.IsNullOrEmpty(path)) {
                return path;
            }

            if (Path.IsPathRooted(path)) {
                return GetRelativePath(path);
            }
            return path;
        }

        private string GetRelativePath(string path) {
            return PathUtility.GetRelativePath(FileSystem.Root, path);
        }
    }
}
