using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using System;
using Microsoft.Azure.Management.IotHub;
using Microsoft.Azure.Management.IotHub.Models;
using Microsoft.Rest;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest.Azure;

namespace Company.Function
{
    public static class DurableFunctionsOrchestrationCSharp1
    {

         const string IotHubScaleOrchestratorInstanceId = "IotHubScaleOrchestrator_1";
        const string IotHubScaleOrchestratorName = nameof(IotHubScaleOrchestrator);
        const string IotHubScaleWorkerName = nameof(IotHubScaleWorker);

        // function configuration and authentication data
        // hard coded for the sample.  For production, look at something like KeyVault for storing secrets
        // more info here-> https://blogs.msdn.microsoft.com/dotnet/2016/10/03/storing-and-using-secrets-in-azure/
        const double JobFrequencyMinutes = 5;
        static string ApplicationId = "<application id>";
        static string SubscriptionId = "<subscription id>";
        static string TenantId = "<tenant id>";
        static string ApplicationPassword = "<application password>";
        static string ResourceGroupName = "<resource group containing iothub>";
        static string IotHubName = "<short iothub name>";
        static int ThresholdPercentage = 90;

        [FunctionName("IotHubScaleInit")]
        public static async Task IotHubScaleInit(
            [TimerTrigger("5 * * * * *")]TimerInfo myTimer,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log)
        {
            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("IotHubScaleOrchestratorName", null);

            log.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");

            //await starter.StartNewAsync(IotHubScaleOrchestratorName, IotHubScaleOrchestratorInstanceId, input: null);
            var existingInstance = await starter.GetStatusAsync(IotHubScaleWorkerName);
            if (existingInstance == null)
            {
                log.LogInformation(String.Format("{0} job not running, starting new instance...", IotHubScaleOrchestratorInstanceId));
                await starter.StartNewAsync("IotHubScaleOrchestratorName",null);
            }
            else
                log.LogInformation(String.Format("An instance of {0} job is already running, nothing to do...", IotHubScaleOrchestratorInstanceId));
        }

        [FunctionName("IotHubScaleOrchestratorName")]
        public static async Task IotHubScaleOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context,
            ILogger log)
        {
            log.LogInformation("IotHubScaleOrchestrator started");

            // launch and wait on the "worker" function
            await context.CallActivityAsync("IotHubScaleWorkerName",null);
            log.LogInformation(String.Format("IotHubScaleOrchestrator done...  tee'ing up next instance in {0} minutes.", JobFrequencyMinutes.ToString()));
        }

        [FunctionName("IotHubScaleWorkerName")]
        public static void IotHubScaleWorker([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            IotHubClient client = GetNewIotHubClient(log);
            if (client == null)
            {
                log.LogError("Unable to create IotHub client");
                return;
            } 

            IotHubDescription desc = client.IotHubResource.Get(ResourceGroupName, IotHubName);
            string currentSKU = desc.Sku.Name;
            long currentUnits = desc.Sku.Capacity;

            // get current "used" message count for the IotHub
            long currentMessageCount = -1;
            IPage<IotHubQuotaMetricInfo> mi = client.IotHubResource.GetQuotaMetrics(ResourceGroupName, IotHubName);
            foreach (IotHubQuotaMetricInfo info in mi)
            {
                if (info.Name == "TotalMessages")
                    currentMessageCount = (long) info.CurrentValue;
            }
            if(currentMessageCount < 0)
            {
                log.LogError("Unable to retreive current message count for IoTHub");
                return;
            }

            // compute the desired message threshold for the current sku
            long messageLimit = GetSkuUnitThreshold(desc.Sku.Name, desc.Sku.Capacity, ThresholdPercentage);

            log.LogInformation("Current SKU Tier: " + desc.Sku.Tier);
            log.LogInformation("Current SKU Name: " + currentSKU);
            log.LogInformation("Current SKU Capacity: " + currentUnits.ToString());
            log.LogInformation("Current Message Count:  " + currentMessageCount.ToString());
            log.LogInformation("Current Sku/Unit Message Threshold:  " + messageLimit);

            // if we are below the threshold, nothing to do, bail
            if (currentMessageCount < messageLimit)
            {
                log.LogInformation(String.Format("Current message count of {0} is less than the threshold of {1}. Nothing to do", currentMessageCount.ToString(), messageLimit));
                return;
            }
            else 
                log.LogInformation(String.Format("Current message count of {0} is over the threshold of {1}. Need to scale IotHub", currentMessageCount.ToString(), messageLimit));

            // figure out what new sku level and 'units' we need to scale to
            string newSkuName = desc.Sku.Name;
            long newSkuUnits = GetScaleUpTarget(desc.Sku.Name, desc.Sku.Capacity);
            if (newSkuUnits < 0)
            {
                log.LogError("Unable to determine new scale units for IoTHub (perhaps you are already at the highest units for a tier?)");
                return;
            }

            // update the IoT Hub description with the new sku level and units
            desc.Sku.Name = newSkuName;
            desc.Sku.Capacity = newSkuUnits;

            // scale the IoT Hub by submitting the new configuration (tier and units)
            DateTime dtStart = DateTime.Now;
            client.IotHubResource.CreateOrUpdate(ResourceGroupName, IotHubName, desc);
            TimeSpan ts = new TimeSpan(DateTime.Now.Ticks - dtStart.Ticks);

            log.LogInformation(String.Format("Updated IoTHub {0} from {1}-{2} to {3}-{4} in {5} seconds", IotHubName, currentSKU, currentUnits, newSkuName, newSkuUnits, ts.Seconds));

        }

        private static IotHubClient GetNewIotHubClient(ILogger log)
        {
            var authContext = new AuthenticationContext(string.Format("https://login.microsoftonline.com/{0}", TenantId));
            var credential = new ClientCredential(ApplicationId, ApplicationPassword);
            AuthenticationResult token = authContext.AcquireTokenAsync("https://management.core.windows.net/", credential).Result;
            if (token == null)
            {
                log.LogError("Failed to obtain the authentication token");
                return null;
            }

            var creds = new TokenCredentials(token.AccessToken);
            var client = new IotHubClient(creds);
            client.SubscriptionId = SubscriptionId;

            return client;
        }

        // get the new sku/units target for scaling the IoT Hub
        public static long GetScaleUpTarget(string currentSku, long currentUnits)
        {
            switch (currentSku)
            {
                case "S1":
                    if (currentUnits <= 199)  // 200 units is the maximum for S1 without involving Azure support
                        return currentUnits++;
                    else
                        return -1;
                case "S2":
                    if (currentUnits <= 199)  // 200 units is the maximum for S2 without involving Azure support
                        return currentUnits++;
                    else
                        return -1;
                case "S3":
                    if (currentUnits <= 9)  // can't have more than 10 S3 units without involving Azure support
                        return currentUnits++;
                    else
                        return -1;
            }
            return -1;   // shouldn't get here unless an invalid Sku was specified
        }

        // get the number of messages/day for the sku/unit/threshold combination
        public static long GetSkuUnitThreshold(string sku, long units, int percent)
        {
            long multiplier = 0;
            switch (sku)
            {
                case "S1":
                    multiplier = 400000;
                    break;
                case "S2":
                    multiplier = 6000000;
                    break;
                case "S3":
                    multiplier = 300000000;
                    break;
            }
            return (long)(multiplier * units * percent) / 100;
        }
    }
}