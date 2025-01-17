﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RestSharp;
using System.Runtime.Serialization.Json;
using System.Runtime.Serialization;
using System.IO;
using System.IO.Compression;
using System.Net.Mail;

namespace BulkExportDownload
{
    class Program
    {
        static internal string emailBody;
        static internal List<SaveLocation> saveLocations = new List<SaveLocation>();
        const string backUpDirPrefix = "back_up_";
        static void Main(string[] args)
        {
            //populate list of SaveLocation with the SaveLocations setting
            string[] saveLocs = Properties.Settings.Default.SaveLocations.Split(';');
            foreach (string saveLoc in saveLocs)
            {
                string[] splitLoc = saveLoc.Split(',');
                string id = splitLoc[0].Trim();
                string savePath = splitLoc[1].Trim();
                SaveLocation saveLocation = new SaveLocation(id, savePath);
                saveLocations.Add(saveLocation);
            }

            emailBody = "";
            string credentials = "credentials u:" + (Properties.Settings.Default.Username) + " " + "pwd:" + (Properties.Settings.Default.Password);
            RestClient client = new RestClient(Properties.Settings.Default.Url);

            foreach (SaveLocation saveLoc in saveLocations)
            {
                string bulkExportId = saveLoc.id;
                string savePath = saveLoc.location;
                string zipPath = savePath + "\\" + bulkExportId + ".zip";

                BulkExport bulkExport = readBulkExportFromApi(client, bulkExportId, credentials);
                if (bulkExport == null)
                {
                    string message = "Could not retrieve bulkexport with id " + bulkExportId;
                    Console.WriteLine(message);
                    addMessageToEmailBody(message);
                    continue; //if we could not retrieve this bulkexport we continue with the next bulkexport
                }

                if (!bulkExport.can_download)
                {
                    //send e-mailmessage that bulk export settings are not correct for download
                    string message = "The bulkexport " + bulkExport.name + " is not configured to allow download (manually of via the API)";
                    Console.WriteLine(message);
                    addMessageToEmailBody(message);
                    continue;
                }


                if ((bulkExport.state == "ready")||((bulkExport.state == "readyWithErrors") && (Properties.Settings.Default.DownloadReadyWithErrors)))
                {
                    //Continue if state=ready or if state is readyWithErrors but only if user wants it
                    try
                    {
                        if (!Properties.Settings.Default.DownloadOnly)
                        {
                            DeleteBackups(bulkExportId, savePath);
                            BackupBulkExport(savePath);
                        }

                        //download new bulkexport, continue to next bulkexport if this fails. Errormessage is added to mail inside the DownloadBulkExport method
                        if (!DownloadBulkExport(client, bulkExportId, credentials, zipPath, bulkExport.name))
                            continue;

                        if (!Properties.Settings.Default.DownloadOnly)
                        {
                            //extract new bulkexport
                            ExtractBulkExport(zipPath, savePath, Properties.Settings.Default.OverwriteFiles);

                            //delete backup if download en extract succeeds
                            DeleteBackups(bulkExportId, savePath);
                            DeleteZip(zipPath);
                        }
                    }
                    catch (Exception e)
                    {
                        string message = "An error occurred when handling the bulkexport " + bulkExport.name + ":\n" + e.Message;
                        Console.WriteLine(message);
                        //addMessageToEmailBody(message);
                        continue;
                    }
                }
                else
                {
                    string message = "The bulkexport '" + bulkExport.name + "' could not be downloaded, because the status of the bulkexport is '" + bulkExport.state + "'";
                    Console.WriteLine(message);
                    addMessageToEmailBody(message);
                }
            }
            if (emailBody != "")
            {
                SendEmail();
            }

            Console.WriteLine("done");
            if (Properties.Settings.Default.DebugMode)
                Console.ReadLine();
        }

        static private BulkExport readBulkExportFromApi(RestClient client, string id, string credentials)
        {
            Console.WriteLine("reading bulkexport " + id + " from API");
            RestRequest request = new RestRequest("api/documents/bulk_exports/{bulk_export_id}", Method.GET);
            request.AddUrlSegment("bulk_export_id", id); // replaces matching token in request.Resource

            // easily add HTTP Headers
            request.AddHeader("Authorization", credentials);
            request.AddHeader("x-api-version", "1");
            request.AddHeader("x-api_key", Properties.Settings.Default.ApiKey);

            IRestResponse response = client.Execute(request);

            if (response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                string content = response.Content; // raw content as string
                BulkExport deserializedBulkExport = new BulkExport();
                System.IO.MemoryStream ms = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(content));
                DataContractJsonSerializer ser = new DataContractJsonSerializer(deserializedBulkExport.GetType());
                deserializedBulkExport = ser.ReadObject(ms) as BulkExport;
                ms.Close();
                return deserializedBulkExport;
            }
            else
            {
                string message = "Could not download zip-file, status code: " + response.StatusCode;
                if (response.ErrorMessage != null)
                {
                    message += ":\n " + response.ErrorMessage;
                }
                Console.WriteLine(message);
                addMessageToEmailBody(message);
                return null;
            }

        }

        static private void DeleteBackups(string bulkExportId, string savePath)
        {
            Console.WriteLine("deleting backups for bulkexport " + bulkExportId);

            //delete old extract directories
            DirectoryInfo directoryInfo = new DirectoryInfo(savePath);
            DirectoryInfo[] backUpDirs = directoryInfo.GetDirectories(backUpDirPrefix + "*");
            foreach (DirectoryInfo dir in backUpDirs)
            {
                try
                {
                    dir.Delete(true);
                }
                catch
                { //dont care
                }
            }
        }

        static private void DeleteZip(string zipPath)
        {
            FileInfo zipInfo = new FileInfo(zipPath);
            zipInfo.Delete();
        }


        private static void BackupBulkExport(string savePath)
        {
            Console.WriteLine("creating backup");

            if (Directory.Exists(savePath))
            {
                string backUpDir = backUpDirPrefix + DateTime.Now.ToString("yyyy-mm-dd_HH.mm.ss");
                Directory.CreateDirectory(Path.Combine(savePath,backUpDir));

                foreach(string filepath in Directory.GetFiles(savePath))
                {
                    string fileName = Path.GetFileName(filepath);
                    string targetFilePath = Path.Combine(savePath, backUpDir, fileName);
                    File.Move(filepath, targetFilePath);
                }
            }
        }

        static private bool DownloadBulkExport(RestClient client, string id, string credentials, string zipPath, string bulkExportName)
        {
            bool blnSuccess = false;

            Console.WriteLine("downloading zip-file for bulkexport " + id);
            using (var fileStream = new FileStream(zipPath, FileMode.Create))
            {
                RestRequest request = new RestRequest("api/documents/bulk_exports/{bulk_export_id}/download", Method.GET)
                {
                    ResponseWriter = (responseStream) => responseStream.CopyTo(fileStream)
                };
                request.AddUrlSegment("bulk_export_id", id); // replaces matching token in request.Resource

                // easily add HTTP Headers
                request.AddHeader("Authorization", credentials);
                request.AddHeader("x-api-version", "1");
                request.AddHeader("x-api_key", Properties.Settings.Default.ApiKey);

                client.DownloadData(request);

                if (fileStream.Length > 0)
                    blnSuccess = true;
                else
                    blnSuccess = false;
            }

            if (!blnSuccess)
            {
                string message = "Could not download zip-file for bulkexport " + bulkExportName;
                Console.WriteLine(message);
                addMessageToEmailBody(message);
            }

            return blnSuccess;
        }

        static private void ExtractBulkExport(string zipPath, string savePath, Boolean overwrite)
        {
            Console.WriteLine("Extracting zip-file");

            // Normalizes the path.
            savePath = Path.GetFullPath(savePath);

            // Ensures that the last character on the extraction path is the directory separator char.
            // Without this, a malicious zip file could try to traverse outside of the expected extraction path.
            if (!savePath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                savePath += Path.DirectorySeparatorChar;

            // Read zip file and extract.
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    // Gets the full path to ensure that relative segments are removed.
                    string destinationPath = Path.GetFullPath(Path.Combine(savePath, entry.FullName));

                    // Directories are not created, create destination directory.
                    string entryPath = Path.GetDirectoryName(destinationPath);
                    if (!Directory.Exists(entryPath))
                    {
                        Directory.CreateDirectory(entryPath);
                    }

                    // Ordinal match is safest, case-sensitive volumes can be mounted within volumes that are case-insensitive.
                    if (destinationPath.StartsWith(savePath, StringComparison.Ordinal))
                    {
                        entry.ExtractToFile(destinationPath, overwrite);
                    }

                }
            }

        }

        static private void addMessageToEmailBody(string message)
        {
            Console.WriteLine("adding error message to e-mail body ");
            emailBody += message + "\n\n";
        }

        static private void SendEmail()
        {
            Console.WriteLine("sending e-mail message with errors");

            string from = Properties.Settings.Default.SenderEmailAddress;
            string to = Properties.Settings.Default.ResponseEmailaddress;

            MailMessage mail = new MailMessage(from, to);
            SmtpClient client = new SmtpClient();
            mail.IsBodyHtml = false;
            client.Port = Properties.Settings.Default.MailPort;
            client.DeliveryMethod = SmtpDeliveryMethod.Network;
            if (Properties.Settings.Default.MailSsl)
            {
                client.Credentials = new System.Net.NetworkCredential(Properties.Settings.Default.MailUsername,Properties.Settings.Default.MailPassword);
                client.EnableSsl = true;
            }
            else
            {
                client.UseDefaultCredentials = false;
            }
            client.Host = Properties.Settings.Default.MailServer;
            mail.Subject = "Error downloading bulkexports";
            mail.Body = emailBody;
            client.Send(mail);
        }
    }

    [DataContract]
    internal class BulkExport
    {
        [DataMember]
        internal Int32 id;

        [DataMember]
        internal string name;

        [DataMember]
        internal string state;

        [DataMember]
        internal string last_export_datetime;

        [DataMember]
        internal bool can_download;
    }
}
