﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;

using BetterCms.Core.Security;

using BetterModules.Core.DataAccess;
using BetterCms.Module.MediaManager.Models;

using BetterModules.Core.DataAccess.DataContext;
using BetterModules.Core.DataAccess.DataContext.Fetching;
using BetterModules.Core.Exceptions.DataTier;

using Common.Logging;

namespace BetterCms.Module.MediaManager.Services
{
    /// <summary>
    /// Default media image service.
    /// </summary>
    internal class DefaultMediaService : IMediaService
    {
        private static readonly ILog Log = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// The repository.
        /// </summary>
        private readonly IRepository repository;

        private readonly IUnitOfWork unitOfWork;

        private readonly IAccessControlService accessControlService;

        private readonly ICmsConfiguration configuration;

        private readonly IMediaFileService fileService;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultMediaService"/> class.
        /// </summary>
        public DefaultMediaService(IRepository repository, IUnitOfWork unitOfWork, IAccessControlService accessControlService, ICmsConfiguration configuration, IMediaFileService fileService)
        {
            this.repository = repository;
            this.unitOfWork = unitOfWork;
            this.accessControlService = accessControlService;
            this.configuration = configuration;
            this.fileService = fileService;
        }

        /// <summary>
        /// Deletes the media.
        /// </summary>
        public bool DeleteMedia(Guid id, int version, bool checkSecurity, IPrincipal currentPrincipal = null)
        {
            var media = repository.AsQueryable<Media>(f => f.Id == id).FirstOne();
            if (version > 0 && media.Version != version)
            {
                throw new ConcurrentDataException(media);
            }

            var file = media as MediaFile;
            var folder = media as MediaFolder;

            if (file != null)
            {
                return DeleteFile(file, checkSecurity, currentPrincipal);
            }

            if (folder != null)
            {
                return DeleteFolder(folder, checkSecurity, currentPrincipal);
            }

            Log.WarnFormat("Media with id={0} is unknown type, so it may be deleted incorrectly.", id);
            return DeleteUnknownTypeMedia(media);
        }

        private bool DeleteFile(MediaFile file, bool checkSecurity, IPrincipal currentPrincipal)
        {
            var allVersions = repository
                .AsQueryable<MediaFile>(f => f.Id == file.Id || f.Original.Id == file.Id)
                .Fetch(f => f.AccessRules)
                .Fetch(f => f.Categories)
                .Fetch(f => f.MediaTags)
                .ToList();

            var mainFile = allVersions.FirstOrDefault(t => t.Id == file.Id);

            if (mainFile == null)
            {
                return true; // Already does not exist.
            }

            try
            {
                // Demand access
                if (checkSecurity && configuration.Security.AccessControlEnabled)
                {
                    accessControlService.DemandAccess(mainFile, currentPrincipal, AccessLevel.ReadWrite);
                }
                fileService.MoveFileToTrashFolder(allVersions);
            }
            catch (Exception ex)
            {
                Log.ErrorFormat("Failed to delete file with id={0}.", ex, mainFile.Id);
                return false;
            }

            Events.MediaManagerEvents.Instance.OnMediaFileDeleted(mainFile);
            return true;
        }

        private bool DeleteFolder(MediaFolder folder, bool checkSecurity, IPrincipal currentPrincipal)
        {
            try
            {
                var allItemsDeleted = true;
                var folderItems = folder.Medias.ToList();
                foreach (var item in folderItems)
                {
                    var fileToDelete = item as MediaFile;
                    if (fileToDelete != null && !DeleteFile(fileToDelete, checkSecurity, currentPrincipal))
                    {
                        allItemsDeleted = false;
                    }
                    var folderToDelete = item as MediaFolder;
                    if (folderToDelete != null && !DeleteFolder(folderToDelete, checkSecurity, currentPrincipal))
                    {
                        allItemsDeleted = false;
                    }
                }

                if (!allItemsDeleted)
                {
                    return false;
                }

                unitOfWork.BeginTransaction();
                repository.AsQueryable<MediaFolder>(f => f.Id == folder.Id || f.Original.Id == folder.Id)
                    .Fetch(f => f.Categories)
                    .Fetch(f => f.MediaTags)
                    .ToList()
                    .ForEach(
                        media =>
                            {
                                if (media.MediaTags != null)
                                {
                                    foreach (var mediaTag in media.MediaTags)
                                    {
                                        repository.Delete(mediaTag);
                                    }
                                }
                                if (media.Categories != null)
                                {
                                    foreach (var category in media.Categories)
                                    {
                                        repository.Delete(category);
                                    }
                                }

                                repository.Delete(media);
                            });
                unitOfWork.Commit();
            }
            catch (Exception ex)
            {
                Log.ErrorFormat("Failed to delete folder with id={0}.", ex, folder.Id);
                return false;
            }

            Events.MediaManagerEvents.Instance.OnMediaFolderDeleted(folder);
            return true;
        }

        private bool DeleteUnknownTypeMedia(Media media)
        {
            unitOfWork.BeginTransaction();

            if (media.MediaTags != null)
            {
                foreach (var mediaTag in media.MediaTags)
                {
                    repository.Delete(mediaTag);
                }
            }

            if (media.Categories != null)
            {
                foreach (var category in media.Categories)
                {
                    repository.Delete(category);
                }
            }

            repository.Delete(media);

            unitOfWork.Commit();

            return true;
        }

        /// <summary>
        /// Archives the sub medias.
        /// </summary>
        /// <param name="media">The media.</param>
        /// <param name="archivedMedias">The archived medias.</param>
        public void ArchiveSubMedias(Media media, List<Media> archivedMedias)
        {
            var subItems = repository.AsQueryable<Media>().Where(m => m.Folder != null && m.Folder.Id == media.Id).ToList();
            foreach (var subItem in subItems)
            {
                if (!subItem.IsArchived)
                {
                    subItem.IsArchived = true;
                    archivedMedias.Add(subItem);

                    repository.Save(subItem);
                }

                ArchiveSubMedias(subItem, archivedMedias);
            }
        }

        /// <summary>
        /// Unarchives the sub medias.
        /// </summary>
        /// <param name="media">The media.</param>
        /// <param name="unarchivedMedias">The unarchived medias.</param>
        public void UnarchiveSubMedias(Media media, List<Media> unarchivedMedias)
        {
            var subItems = repository.AsQueryable<Media>().Where(m => m.Folder != null && m.Folder.Id == media.Id).ToList();
            foreach (var subItem in subItems)
            {
                if (subItem.IsArchived)
                {
                    subItem.IsArchived = false;
                    unarchivedMedias.Add(subItem);

                    repository.Save(subItem);
                }

                UnarchiveSubMedias(subItem, unarchivedMedias);
            }
        }
    }
}