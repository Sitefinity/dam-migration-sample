# Migration of existing Sitefinity media content to DAM - Frontify Sample
This repository contains a sample code to demonstrate how to migrate media content from existing libraries in Sitefinity CMS to a DAM provider.
The sample implementation is for Frontify but it could be used as reference on how to migrate items to any DAM provider integrated with Sitefinity.

Migration of existing media content to a DAM provider will change the actual storage of the files. Instead stored and managed by Sitefinity they will be managed by the digital assets management system. During the process of migration, the existing media content items will be moved to a new library in Sitefinity, and the files will be moved to the DAM provider. The IDs of the items will remain unchanged so existing relations between the media items and any other content will be preserved.
> The new library of the media items won't be visible in Sitefinity UI.

Migration moves items from one library at a time, so migration must be executed for each library that you want to migrate.

To migrate a library, you must install Progress.Sitefinity.Libraries.DamMigration nuget package.
The migration itself consists of two main pillars:
1. **DamMigrator** class - responsible for updating existing media in Sitefinity, distributed with Progress.Sitefinity.Libraries.DamMigration nuget package.
2. A class which implements **IDamUploader** interface - responsible for uploading the files from Sitefinity to the DAM provider (the interface is also distributed with Progress.Sitefinity.Libraries.DamMigration nuget package). This logic is specific for each DAM provider and must be implemented for the provider that you are using.
> In this repository we have prepared a sample implementation for Frontify - **FrontifyUploader** class. If your DAM is Frontify you could use this implementation or substitute it with your own.

To start a migration all you have to do is to call **DamMigrator**'s static method _StartMigrationTask_.
It accepts four parameters:
1. _uploader_ - An instance of a class which implements **IDamUploader** interface.
2. _libraryType_ - The type of the library which media items will be migrated.
3. _libraryId_ - The ID of the library which media items will be migrated.
4. _providerName_ - The name of the data provider of the library.

Here is a sample of migrating an image library:
```cs
...
  // the root path of the directory in Frontify where Sitefinity's library will be moved to -> "Sitefinity Media/Images".
  string[] rootDirectories = new string[] { "Sitefinity Media", "Images" };
  string token = "K2xwjPRaDjhWxJBzMds5YUgwaTRqwerTyL3SauP8"; // authentication token for Frontify
  string projectId = "eyPxHPQasDlkjHGfZxc9PoI7QWR1tYUiOpmnbVCxzdSaeFV5"; // the ID of the project in Frontify

  // instantiate FrontifyUploader
  FrontifyUploader frontifyUploader = new FrontifyUploader(rootDirectories, token, projectId);

  LibrariesManager manager = LibrariesManager.GetManager();
  Library imageLibrary = manager.GetAlbums().FirstOrDefault(l => l.Title == "Library to migrate");

  // start migration
  DamMigrator.StartMigrationTask(frontifyUploader, imageLibrary.GetType(), imageLibrary.Id, imageLibrary.Provider.ToString());
...
```
> For videos use GetVideoLibraries() and for documents use GetDocumentLibraries()

_StartMigrationTask_ method will schedule a task to execute the migration and will return the ID of the task.
The task will appear in the media management screens - Images, Documents and Videos, and it's progress can be monitored there.
The task will also be listed in Scheduled Tasks screen.

When the task completes a detailed trace log will be available in the logs folder. In case the task has failed to move any of the items they will remain in the source folder, and you will be able to run the task again by clicking "Retry" in media management screens or from Scheduled Tasks screen.
> When all items are moved to the DAM provider the library will be empty but will not be deleted.
