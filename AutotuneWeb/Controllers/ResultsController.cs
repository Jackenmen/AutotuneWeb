﻿using AutotuneWeb.Models;
using Microsoft.Azure.Batch;
using Microsoft.Azure.Batch.Auth;
using Microsoft.Azure.Batch.Common;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using SendGrid;
using SendGrid.Helpers.Mail;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net;
using System.Web;
using System.Web.Mvc;

namespace AutotuneWeb.Controllers
{
    public class ResultsController : Controller
    {
        public ActionResult JobFinished(int id, string key, string commit)
        {
            // Validate the key
            if (key != ConfigurationManager.AppSettings["ResultsCallbackKey"])
                return HttpNotFound();

            // Connect to Azure Batch
            var credentials = new BatchSharedKeyCredentials(
                ConfigurationManager.AppSettings["BatchAccountUrl"],
                ConfigurationManager.AppSettings["BatchAccountName"],
                ConfigurationManager.AppSettings["BatchAccountKey"]);

            using (var batchClient = BatchClient.Open(credentials))
            using (var con = new SqlConnection(ConfigurationManager.ConnectionStrings["Sql"].ConnectionString))
            {
                con.Open();

                // Load the job details from the database
                var job = Job.Load(con, id);
                if (job == null)
                    return HttpNotFound();

                var result = "";
                var success = false;
                var startTime = (DateTime?)null;
                var endTime = (DateTime?)null;
                var emailBody = (string)null;
                var attachments = new CloudBlob[0];
                
                try
                {
                    var jobName = $"autotune-job-{id}";

                    // Check if the Autotune job finished successfully
                    var autotune = batchClient.JobOperations.GetTask(jobName, "Autotune");
                    success = autotune.ExecutionInformation.ExitCode == 0;
                    startTime = autotune.ExecutionInformation.StartTime;
                    endTime = autotune.ExecutionInformation.EndTime;
                    
                    // Connect to Azure Storage
                    var connectionString = ConfigurationManager.ConnectionStrings["Storage"].ConnectionString;
                    var storageAccount = CloudStorageAccount.Parse(connectionString);
                    var cloudBlobClient = storageAccount.CreateCloudBlobClient();

                    // Find the log file produced by Autotune
                    var container = cloudBlobClient.GetContainerReference(jobName);

                    if (success)
                    {
                        var blob = container.GetBlobReference("autotune_recommendations.log");

                        using (var stream = new MemoryStream())
                        using (var reader = new StreamReader(stream))
                        {
                            // Download the log file
                            blob.DownloadToStream(stream);
                            stream.Position = 0;

                            result = reader.ReadToEnd();

                            // Parse the results
                            var parsedResults = AutotuneResults.ParseResult(result, job);

                            emailBody = RenderViewToString(ControllerContext, "Success", parsedResults);
                        }
                    }

                    // Get the log files to attach to the email
                    attachments = container.ListBlobs()
                        .Select(b => new CloudBlockBlob(b.Uri, cloudBlobClient))
                        .Where(b => b.Name != "autotune_recommendations.log" && b.Name != "profile.json")
                        .ToArray();
                }
                catch (Exception ex)
                {
                    result = ex.ToString();
                    success = false;
                }

                if (emailBody == null)
                    emailBody = RenderViewToString(ControllerContext, "Failure");

                EmailResults(job.EmailResultsTo, emailBody, attachments);

                // Update the details in the SQL database
                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandText = "UPDATE Jobs SET ProcessingStarted = @ProcessingStarted, ProcessingCompleted = @ProcessingCompleted, Result = @Result, Failed = @Failed WHERE JobID = @Id";
                    cmd.Parameters.AddWithValue("@Id", id);
                    cmd.Parameters.AddWithValue("@ProcessingStarted", (object)startTime ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ProcessingCompleted", (object)endTime ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Result", result);
                    cmd.Parameters.AddWithValue("@Failed", !success);
                    cmd.ExecuteNonQuery();
                }

                // Store the commit hash to identify the version of Autotune that was used
                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandText = "UPDATE Settings SET Value = @commit WHERE Name = 'Commit'";
                    cmd.Parameters.AddWithValue("@Commit", commit);
                    cmd.ExecuteNonQuery();
                }
            }

            return Content("");
        }

        private void EmailResults(string emailAddress, string emailBody, CloudBlob[] attachments)
        {
            var apiKey = ConfigurationManager.AppSettings["SendGridApiKey"];
            var client = new SendGridClient(apiKey);
            var from = new EmailAddress(ConfigurationManager.AppSettings["SendGridFromAddress"], "Autotune");
            var subject = "Autotune Results";
            var to = new EmailAddress(emailAddress);
            var msg = MailHelper.CreateSingleEmail(from, to, subject, "", emailBody);

            foreach (var attachment in attachments)
            {
                using (var stream = new MemoryStream())
                {
                    attachment.DownloadToStream(stream);
                    msg.AddAttachment(attachment.Name, Convert.ToBase64String(stream.ToArray()));
                }
            }
            
            client.SendEmailAsync(msg).Wait();
        }

        static string RenderViewToString(ControllerContext context,
                                    string viewPath,
                                    object model = null,
                                    bool partial = false)
        {
            // first find the ViewEngine for this view
            ViewEngineResult viewEngineResult = null;
            if (partial)
                viewEngineResult = ViewEngines.Engines.FindPartialView(context, viewPath);
            else
                viewEngineResult = ViewEngines.Engines.FindView(context, viewPath, null);

            if (viewEngineResult == null)
                throw new FileNotFoundException("View cannot be found.");

            // get the view and attach the model to view data
            var view = viewEngineResult.View;
            context.Controller.ViewData.Model = model;

            string result = null;

            using (var sw = new StringWriter())
            {
                var ctx = new ViewContext(context, view,
                                            context.Controller.ViewData,
                                            context.Controller.TempData,
                                            sw);
                view.Render(ctx, sw);
                result = sw.ToString();
            }

            return result;
        }
    }
}