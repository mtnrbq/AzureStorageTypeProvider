﻿///Contains reusable helper functions for accessing blobs
module internal FSharp.Azure.StorageTypeProvider.Blob.BlobRepository

open Microsoft.WindowsAzure.Storage
open Microsoft.WindowsAzure.Storage.Blob
open System
open System.IO

type ContainerItem = 
    | Folder of path : string * name : string * contents : ContainerItem array Lazy
    | Blob of path : string * name : string * blobType : BlobType * length : int64 option

type LightweightContainer = 
    { Name : string
      Contents : ContainerItem seq Lazy }

let getBlobClient connection = CloudStorageAccount.Parse(connection).CreateCloudBlobClient()
let getContainerRef(connection, container) = (getBlobClient connection).GetContainerReference(container)
let getBlockBlobRef (connection, container, file) = getContainerRef(connection, container).GetBlockBlobReference(file)
let getPageBlobRef (connection, container, file) = getContainerRef(connection, container).GetPageBlobReference(file)

let private getItemName (item : string) (parent : CloudBlobDirectory) = 
    item, 
    match parent with
    | null -> item
    | parent -> item.Substring(parent.Prefix.Length)

let rec private getContainerStructure wildcard (container : CloudBlobContainer) = 
    container.ListBlobs(prefix = wildcard)
    |> Seq.distinctBy (fun b -> b.Uri.AbsoluteUri)
    |> Seq.choose (function
       | :? CloudBlobDirectory as directory -> 
           let path, name = getItemName directory.Prefix directory.Parent
           Some(Folder(path, name, lazy(container |> getContainerStructure directory.Prefix)))
       | :? ICloudBlob as blob ->
           let path, name = getItemName blob.Name blob.Parent
           Some(Blob(path, name, blob.BlobType, Some blob.Properties.Length))
       | _ -> None)
    |> Seq.toArray

let listBlobs incSubDirs (container:CloudBlobContainer) prefix = 
    container.ListBlobs(prefix, incSubDirs)
    |> Seq.choose(function
        | :? ICloudBlob as blob -> 
            let path, name = getItemName blob.Name blob.Parent
            Some(Blob(path, name, blob.Properties.BlobType, Some blob.Properties.Length))
        | _ -> None)    //can safely ignore folder types as we have a flat structure if & only if we want to include items from sub directories

let getBlobStorageAccountManifest connection = 
    (getBlobClient connection).ListContainers()
    |> Seq.toList
    |> List.map (fun container -> 
        { Name = container.Name
          Contents =
            lazy
                container
                |> getContainerStructure null
                |> Seq.cache })

let awaitUnit = Async.AwaitIAsyncResult >> Async.Ignore

let downloadFolder (connectionDetails, path) =
    let downloadFile (blobRef:ICloudBlob) destination =
        let targetDirectory = Path.GetDirectoryName(destination)
        if not (Directory.Exists targetDirectory) then Directory.CreateDirectory targetDirectory |> ignore
        blobRef.DownloadToFileAsync(destination, FileMode.Create) |> awaitUnit

    let connection, container, folderPath = connectionDetails
    let containerRef = (getBlobClient connection).GetContainerReference(container)
    containerRef.ListBlobs(prefix = folderPath, useFlatBlobListing = true)
    |> Seq.choose (function
        | :? ICloudBlob as b -> Some b
        | _ -> None)
    |> Seq.map (fun blob -> 
        let targetName = 
            match folderPath with
            | folderPath when String.IsNullOrEmpty folderPath -> blob.Name
            | _ -> blob.Name.Replace(folderPath, String.Empty)
        downloadFile blob (Path.Combine(path, targetName)))
    |> Async.Parallel
    |> Async.Ignore