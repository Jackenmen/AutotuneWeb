﻿using AutotuneWeb.Models;
using Microsoft.Azure.Batch;
using Microsoft.Azure.Batch.Auth;
using Microsoft.Azure.Batch.Common;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Web;
using System.Web.Mvc;

namespace AutotuneWeb.Controllers
{
    public class HomeController : Controller
    {
        public ActionResult Index()
        {
            var url = Request.Cookies["nsUrl"]?.Value;
            return View((object)url);
        }

        [HttpPost]
        public ActionResult Autotune(Uri nsUrl, decimal? cr, decimal? sens)
        {
            if (nsUrl == null)
            {
                ModelState.AddModelError(nameof(nsUrl), "Nightscout URL is required");
                return View("Index");
            }

            Response.Cookies.Add(new HttpCookie("nsUrl", nsUrl.ToString()));
            NSProfileDetails nsProfile;

            try
            {
                nsProfile = NSProfileDetails.LoadFromNightscout(ref nsUrl);
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(nameof(nsUrl), ex.Message);
                return View("Index", (object) nsUrl.ToString());
            }

            ModelState.SetModelValue(nameof(nsUrl), new ValueProviderResult(nsUrl, nsUrl.ToString(), CultureInfo.InvariantCulture));
            ViewBag.NSUrl = nsUrl;

            nsProfile.CarbRatio = CombineAdjacentTimeBlocks(nsProfile.CarbRatio);
            nsProfile.Sensitivity = CombineAdjacentTimeBlocks(nsProfile.Sensitivity);

            ViewBag.Units = nsProfile.Units;
            ViewBag.ProfileName = nsProfile.Name;

            if (cr != null)
                nsProfile.CarbRatio = new[] { new NSValueWithTime { TimeAsSeconds = 0, Value = cr.Value } };

            if (sens != null)
                nsProfile.Sensitivity = new[] { new NSValueWithTime { TimeAsSeconds = 0, Value = sens.Value } };

            // Check for multiple IC and ISF ratios
            if (nsProfile.CarbRatio.Length > 1 || nsProfile.Sensitivity.Length > 1)
                return View("MultipleValues", nsProfile);

            var oapsProfile = nsProfile.MapToOaps();

            var warnings = new List<string>();

            if (oapsProfile.Dia < 3)
            {
                warnings.Add($"DIA of {oapsProfile.Dia} hours is too short. It has been automatically adjusted to the minimum of 3 hours");
                oapsProfile.Dia = 3;
            }

            if (!TempBasalIncludesRateProperty(nsUrl))
                warnings.Add("Temporary basal records in this Nightscout instance do not include a \"rate\" property - they will not be taken into account by Autotune");

            ViewBag.Warnings = warnings;
            ViewBag.TimeZone = nsProfile.TimeZone;
            ViewBag.Email = Request.Cookies["email"]?.Value;
            return View("Converted", oapsProfile);
        }

        class NSTempBasal
        {
            public decimal? Rate { get; set; }
        }

        private bool TempBasalIncludesRateProperty(Uri url)
        {
            var profileSwitchUrl = new Uri(url, "/api/v1/treatments.json?find[eventType][$eq]=Temp%20Basal&count=10");
            var req = WebRequest.CreateHttp(profileSwitchUrl);
            using (var resp = req.GetResponse())
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream))
            {
                var json = reader.ReadToEnd();
                var tempBasals = JsonConvert.DeserializeObject<NSTempBasal[]>(json);

                return tempBasals.Length == 0 || tempBasals.Any(tb => tb.Rate != null);
            }
        }

        private NSValueWithTime[] CombineAdjacentTimeBlocks(NSValueWithTime[] values)
        {
            var combined = new List<NSValueWithTime>();

            for (var i = 0; i < values.Length; i++)
            {
                if (i == 0 || combined.Last().Value != values[i].Value)
                    combined.Add(values[i]);
            }

            return combined.ToArray();
        }

        public ActionResult RunJob(Uri nsUrl, string oapsProfile, string units, string timezone, bool? uamAsBasal, decimal pumpBasalIncrement, decimal? min5MCarbImpact, string curve, string emailResultsTo, int days)
        {
            if (min5MCarbImpact != null || !String.IsNullOrEmpty(curve))
            {
                var profile = JsonConvert.DeserializeObject<OapsProfile>(oapsProfile);

                if (min5MCarbImpact != null)
                    profile.Min5mCarbImpact = min5MCarbImpact.Value;

                if (!String.IsNullOrEmpty(curve))
                    profile.InsulinCurve = curve;

                oapsProfile = JsonConvert.SerializeObject(profile);
            }

            // Cap the number of days to run Autotune over
            days = Math.Min(days, 30);

            // Save the details of this job in the database
            int queuePos;
            using (var con = new SqlConnection(ConfigurationManager.ConnectionStrings["Sql"].ConnectionString))
            using (var cmd = con.CreateCommand())
            {
                con.Open();

                cmd.Parameters.AddWithValue("@NSUrl", nsUrl.ToString());
                cmd.Parameters.AddWithValue("@Profile", oapsProfile.Replace("\r\n", "\n"));
                cmd.Parameters.AddWithValue("@Units", units);
                cmd.Parameters.AddWithValue("@TimeZone", timezone);
                cmd.Parameters.AddWithValue("@UAMAsBasal", uamAsBasal.GetValueOrDefault());
                cmd.Parameters.AddWithValue("@PumpBasalIncrement", pumpBasalIncrement);
                cmd.Parameters.AddWithValue("@EmailResultsTo", emailResultsTo);
                cmd.Parameters.AddWithValue("@Days", days);

                // Check if the same job is already running
                cmd.CommandText = "SELECT JobID FROM Jobs WHERE NSUrl = @NSUrl AND Profile = @Profile AND CategorizeUAMAsBasal = @UAMAsBasal AND ProcessingCompleted IS NULL";
                var existingId = 0;
                DateTime? existingJobStarted = null;
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        existingId = reader.GetInt32(0);
                }

                if (existingId != 0)
                {
                    // Get the job details from Azure Batch
                    var credentials = new BatchSharedKeyCredentials(
                        ConfigurationManager.AppSettings["BatchAccountUrl"],
                        ConfigurationManager.AppSettings["BatchAccountName"],
                        ConfigurationManager.AppSettings["BatchAccountKey"]);

                    using (var batchClient = BatchClient.Open(credentials))
                    {
                        var existingJobName = $"autotune-job-{existingId}";
                        var existingTask = batchClient.JobOperations.GetTask(existingJobName, "Autotune");
                        
                        existingJobStarted = existingTask.ExecutionInformation.StartTime;
                        queuePos = batchClient.JobOperations.ListJobs(new ODATADetailLevel(filterClause: "state eq 'active'", selectClause: "id")).Count();
                    }

                    if (existingJobStarted == null)
                        ViewBag.QueuePos = queuePos;

                    ViewBag.JobStarted = existingJobStarted;
                    return View("AlreadyRunning");
                }

                cmd.CommandText = "INSERT INTO Jobs (NSUrl, Profile, CreatedAt, Units, TimeZone, CategorizeUAMAsBasal, PumpBasalIncrement, EmailResultsTo, DaysDuration) VALUES (@NSUrl, @Profile, CURRENT_TIMESTAMP, @Units, @TimeZone, @UAMAsBasal, @PumpBasalIncrement, @EmailResultsTo, @Days)";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "SELECT @@IDENTITY";
                var id = Convert.ToInt32(cmd.ExecuteScalar());
                
                var jobName = $"autotune-job-{id}";
                string containerUrl;
                var profileUrl = SaveProfileToStorage(jobName, oapsProfile, out containerUrl);
                queuePos = CreateBatchJob(jobName, id, profileUrl, containerUrl, nsUrl.ToString(), days, timezone, uamAsBasal.GetValueOrDefault());
            }

            Response.Cookies.Add(new HttpCookie("email", emailResultsTo));

            return View(queuePos);
        }

        private string SaveProfileToStorage(string jobName, string profile, out string containerUrl)
        {
            // Connect to Azure Storage
            var connectionString = ConfigurationManager.ConnectionStrings["Storage"].ConnectionString;
            var storageAccount = CloudStorageAccount.Parse(connectionString);
            var cloudBlobClient = storageAccount.CreateCloudBlobClient();

            // Create container for the job
            var container = cloudBlobClient.GetContainerReference(jobName);
            container.Create();

            // Get a SAS URL for the container. This should allow write access to the container for the next 24 hours only
            // to allow the job to upload results
            var containerSasConstrains = new SharedAccessBlobPolicy
            {
                SharedAccessStartTime = DateTimeOffset.UtcNow.AddMinutes(-5),
                SharedAccessExpiryTime = DateTimeOffset.UtcNow.AddHours(24),
                Permissions = SharedAccessBlobPermissions.Read | SharedAccessBlobPermissions.Write
            };

            // Generate the shared access signature on the container, setting the constraints directly on the signature
            var sasContainerToken = container.GetSharedAccessSignature(containerSasConstrains);
            containerUrl = container.Uri + sasContainerToken;

            // Create the profile.json blob in the container
            var blob = container.GetBlockBlobReference("profile.json");
            blob.UploadText(profile);

            // Get a SAS URL for the blob. This should allow read access to the profile for the next 24 hours only
            var blobSasConstraints = new SharedAccessBlobPolicy
            {
                SharedAccessStartTime = DateTimeOffset.UtcNow.AddMinutes(-5),
                SharedAccessExpiryTime = DateTimeOffset.UtcNow.AddHours(24),
                Permissions = SharedAccessBlobPermissions.Read
            };

            // Generate the shared access signature on the blob, setting the constraints directly on the signature.
            var sasBlobToken = blob.GetSharedAccessSignature(blobSasConstraints);

            // Return the URI string for the blob, including the SAS token.
            return blob.Uri + sasBlobToken;
        }

        private int CreateBatchJob(string jobName, int id, string profileUrl, string containerUrl, string nsUrl, int daysDuration, string timeZone, bool uamAsBasal)
        {
            // Connect to Azure Batch
            var credentials = new BatchSharedKeyCredentials(
                ConfigurationManager.AppSettings["BatchAccountUrl"],
                ConfigurationManager.AppSettings["BatchAccountName"],
                ConfigurationManager.AppSettings["BatchAccountKey"]);

            using (var batchClient = BatchClient.Open(credentials))
            {
                var poolId = ConfigurationManager.AppSettings["BatchPoolId"];

                // Create the job
                var job = batchClient.JobOperations.CreateJob();
                job.Id = jobName;
                job.PoolInformation = new PoolInformation { PoolId = poolId };
                job.UsesTaskDependencies = true;
                job.OnAllTasksComplete = OnAllTasksComplete.TerminateJob;
                job.Commit();

                // Add a task to the job to run Autotune
                var commandLine = "/bin/sh -c '" +
                    "cd \"$AZ_BATCH_TASK_WORKING_DIR\" && " +
                    "mkdir -p settings && " +
                    "mv profile.json settings && " +
                    "cp settings/profile.json settings/pumpprofile.json && " +
                    "cp settings/profile.json settings/autotune.json && " +
                    $"TZ='{timeZone}' && " +
                    "export TZ && " +
                    $"oref0-autotune --dir=$AZ_BATCH_TASK_WORKING_DIR --ns-host={nsUrl} --start-date={DateTime.Now.AddDays(-daysDuration):yyyy-MM-dd} --end-date={DateTime.Now.AddDays(-1):yyyy-MM-dd} --categorize-uam-as-basal={(uamAsBasal ? "true" : "false")}" +
                    "'";

                var task = new CloudTask("Autotune", commandLine);

                // The task needs to use the profile.json file previously added to Azure Storage
                task.ResourceFiles = new List<ResourceFile>();
                task.ResourceFiles.Add(ResourceFile.FromUrl(profileUrl, "profile.json"));

                // Capture the recommendations generated by Autotune into Azure Storage
                task.OutputFiles = new List<OutputFile>();
                task.OutputFiles.Add(new OutputFile(
                    filePattern: "autotune/autotune_recommendations.log", 
                    destination: new OutputFileDestination(new OutputFileBlobContainerDestination(containerUrl)), 
                    uploadOptions: new OutputFileUploadOptions(OutputFileUploadCondition.TaskCompletion)));
                task.OutputFiles.Add(new OutputFile(
                    filePattern: "autotune/autotune.*.log",
                    destination: new OutputFileDestination(new OutputFileBlobContainerDestination(containerUrl)),
                    uploadOptions: new OutputFileUploadOptions(OutputFileUploadCondition.TaskCompletion)));

                // Ensure the task still completes even if Autotune fails, as we still want to email the
                // log file with the error details
                task.ExitConditions = new ExitConditions
                {
                    ExitCodes = new List<ExitCodeMapping>(new[] 
                    {
                        new ExitCodeMapping(1, new ExitOptions { DependencyAction = DependencyAction.Satisfy })
                    })
                };

                batchClient.JobOperations.AddTask(jobName, task);

                // Get the URL for the JobFinished action that the recommendations can be uploaded to to generate the email
                var uploadUrl = new Uri(Request.Url, Url.Action("JobFinished", "Results", new { id, key = ConfigurationManager.AppSettings["ResultsCallbackKey"] }));
                var uploadCommandLine = $"/bin/sh -c '" +
                    "cd /usr/src/oref0 && " +
                    $"wget -O /dev/null -o /dev/null {uploadUrl}\\&commit=$(git rev-parse --short HEAD)" +
                    "'";
                var uploadTask = new CloudTask("Upload", uploadCommandLine);
                uploadTask.DependsOn = TaskDependencies.OnId(task.Id);
                uploadTask.Constraints = new TaskConstraints(maxTaskRetryCount: 2);
                batchClient.JobOperations.AddTask(jobName, uploadTask);

                var queuePos = batchClient.JobOperations.ListJobs(new ODATADetailLevel(filterClause: "state eq 'active'", selectClause: "id")).Count();
                return queuePos;
            }
        }

        public ActionResult About()
        {
            // Get the details of the Autotune commit used for the latest job
            using (var con = new SqlConnection(ConfigurationManager.ConnectionStrings["Sql"].ConnectionString))
            using (var cmd = con.CreateCommand())
            {
                con.Open();

                cmd.CommandText = "SELECT Value FROM Settings WHERE Name = 'Commit'";
                var commit = (string)cmd.ExecuteScalar();

                ViewBag.Commit = commit;
            }

            return View();
        }

        public ActionResult MissingRate()
        {
            return View();
        }

        public ActionResult Privacy()
        {
            return View();
        }

        public ActionResult Delete(string url, string email)
        {
            using (var con = new SqlConnection(ConfigurationManager.ConnectionStrings["Sql"].ConnectionString))
            using (var cmd = con.CreateCommand())
            {
                con.Open();

                cmd.CommandText = "DELETE FROM Jobs WHERE NSUrl = @url AND EmailResultsTo = @email";
                cmd.Parameters.AddWithValue("@url", url);
                cmd.Parameters.AddWithValue("@email", email);

                var deleted = cmd.ExecuteNonQuery();

                return View(deleted);
            }
        }
    }
}