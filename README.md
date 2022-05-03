# DAM Migration Sample
This repository contains a sample code to demonstrate how to migrate media content from existing libraries in Sitefinity CSM to a DAM provider.
The sample implementation is for Frontify but it could be used as reference on how to migrate items to any DAM provider integrated with Sitefinity.

Migration of exising media content to a DAM provider will change the actual storage of the files. Instead stored and managed by Sitefinity they will be managed by the digital assets management system. During the process of migration the existing media content items will be moved to a new library in Sitefinity and the files will be moved to the DAM provider. The IDs of the items will remain unchanged so existing relations between the media items and any other content will be preserved.
> The new library of the media items won't be visible in Sitefinity UI.

Migration moves items from one library at a time so migration have to be executed for each library that you want to migrate.

To migrate librarie you must install DamMigration nuget package.
The migration itslef consists of two main pillars:
1. **DamMigrator** class - responsible for updating existing media in Sitefinity, distributed in DamMigration nuget package.
2. A class which implements **IDamUploader** interface - responsible for uploading the files to the DAM provider (the interface is also distributed by DamMigration nuget package). This logic is specific for each DAM provider and must be implemented for the provider that you are using. 
> In this repository we have prepared a sample implementation for Frontify - **FrontifyUploader** class. If your DAM is Frontify you could use this implementation or substitute it with your own.

To start a migration all you have to do is to call **DamMigtator**'s class static method _Migrate_.
It accepts five parameters:
1. _uploader_ - An instance of a class which implements **IDamUploader** interface.
2. _libraryType_ - The type of the library which media items will be migrated.
3. _libraryId_ - The ID of the library which media items will be migrated.
4. _providerName_ - The name of the data provider of the library. 
5. _errorMessage_ - Out parameter. By default its value is null which indicates successfull strat of the migration and returns an error message if starting the migration fails.
On successfull start the method returns _true_ and if fail it retuns _false_.

Here is a sample of migrating a library: 
```cs
...
  string[] directories = new string[] { "Sitefinity media" }; // the root path of the directory in Frontify where library from Sitefinity will be moved to
  string token = "K2xwjPRaDjhWxJBzMds5YUgwaTRqwerTyL3SauP8"; // authentication token for Frontify
  string projectId = "eyPxHPQasDlkjHGfZxc9PoI7QWR1tYUiOpmnbVCxzdSaeFV5"; // the ID of the project in Frontify
  
  // instantiate FrontifyUploader
  FrontifyUploader frontifyUploader = new FrontifyUploader(directories, token, projectId);
  
  Type libraryType = typeof(Album);
  Guid libraryId = new Guid("082cc07b-c286-4fdc-a735-57aeb43d35cb");
  string providername = "OpenAccessDataProvider";
  string migrateErrorMessage = null;
  
  // start migration
  DamMigrator.Migrate(frontifyUploader, libraryType, libraryId, providerName, out migrateErrorMessage);
...
```

If _Migrate_ method completes without errors it will schedule a task to execute the migration.
The progress of the task can be monitored in media management screens - Images, Documents and Videos.
The task will aslo be listed in Scheduled Tasks screen.

When the task completes a detailed trace log will be available in the logs folder. In case the task has failed to move any of the items they will remain in the folder and you will be able to run the task again by clicking "Retry".
> When all items are moved to the DAM provider the library will be empty but will not be deleted.
