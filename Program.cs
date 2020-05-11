using System;
using System.IO;
using System.Collections.Generic;
using System.Configuration;

using Ionic.Zip;
using AgGateway.ADAPT.PluginManager;
using AgGateway.ADAPT.ApplicationDataModel.ADM;
using AgGateway.ADAPT.ApplicationDataModel.Common;
using AgGateway.ADAPT.ApplicationDataModel.Products;
using AgGateway.ADAPT.ApplicationDataModel.Logistics;
using AgGateway.ADAPT.ApplicationDataModel.Equipment;
using AgGateway.ADAPT.ApplicationDataModel.LoggedData;
using AgGateway.ADAPT.ApplicationDataModel.Representations;
using AgGateway.ADAPT.ApplicationDataModel.Products;

namespace ClimateADAPTPluginDemoApp
{
    class Program
    {
        public const double SQUARE_FEET_PER_ACRE = 43560;

        protected static DirectoryInfo tempDir = null;
        protected static IPlugin plugin = null;

        /// <summary>
        /// Update App.config to change pluginDirectory, sampleFile, pluginName
        /// </summary>
        /// <param name="args"></param>
        static void Main(string[] args)
        {
            Console.WriteLine("More example files can be found at https://dev.fieldview.com/technical-documentation/");
            Setup();

            IList<ApplicationDataModel> admModels = Import();

            foreach (ApplicationDataModel adm in admModels)
            {
                if (adm != null)
                {
                    if (adm.Catalog == null)
                    {
                        throw new Exception("No Catalog found.");
                    }

                    if (adm.Documents == null)
                    {
                        throw new Exception("No Documents found.");
                    }
                }

                Console.WriteLine("File set: " + adm.Catalog.Description);

                // Dump out the logged data
                foreach(LoggedData loggedData in adm.Documents.LoggedData)
                {
                    GetLoggedData(adm.Catalog, loggedData);
                }
            }

            Teardown();
            Console.WriteLine("Demo ran successfully.  Hit any Enter key to close this window.");
            Console.ReadLine();
        }

        public static void Teardown()
        {
            if (tempDir != null && tempDir.Exists)
            {
                tempDir.Delete(true);
                tempDir = null;
            }
        }

        public static void Setup()
        {
            if (!PluginDirectoryExists())
            {
                throw new Exception("Plugin directory not found.");
            }

            if (!SampleFileExists())
            {
                throw new Exception("Samples files not found.");
            }

            LoadPlugin();

            tempDir = CreateTempDir();
            Unzip(ConfigurationManager.AppSettings["sampleFile"], tempDir);
        }

        public static bool PluginDirectoryExists()
        {
            return Directory.Exists(ConfigurationManager.AppSettings["pluginDirectory"]);
        }

        // More example files can be found at https://dev.fieldview.com/technical-documentation/
        public static bool SampleFileExists()
        {
            return File.Exists(ConfigurationManager.AppSettings["sampleFile"]);
        }

        public static void LoadPlugin()
        {
            PluginFactory factory = new PluginFactory(ConfigurationManager.AppSettings["pluginDirectory"]);
            plugin = factory.GetPlugin(ConfigurationManager.AppSettings["pluginName"]);

            plugin.Initialize();
        }

        public static DirectoryInfo CreateTempDir()
        {
            string path = Path.GetTempPath() + Guid.NewGuid().ToString("N");
            DirectoryInfo tempDir = new DirectoryInfo(path);
            tempDir.Create();
            tempDir.Refresh();

            Console.Error.WriteLine(tempDir.FullName);

            return tempDir;
        }

        public static void Unzip(string zipfile, DirectoryInfo tempDir)
        {
            using (ZipFile zip = ZipFile.Read(zipfile))
            {
                zip.ExtractAll(tempDir.FullName);
            }
        }

        public static IList<ApplicationDataModel> Import()
        {
            IList<ApplicationDataModel> admModels = plugin.Import(tempDir.FullName);

            return admModels;
        }

        public static void GetLoggedData(Catalog catalog, LoggedData loggedData)
        {
            Console.WriteLine("Logged Data: " + loggedData.Description);

            // Write out the grower/farm/field
            Grower grower = catalog.Growers.Find(x => x.Id.ReferenceId == loggedData.GrowerId);
            if(grower != null)
            {
                Console.WriteLine("Grower: " + grower.Name);
            }

            Farm farm = catalog.Farms.Find(x => x.Id.ReferenceId == loggedData.FarmId);
            if (farm != null)
            {
                Console.WriteLine("Farm: " + farm.Description);
            }

            Field field = catalog.Fields.Find(x => x.Id.ReferenceId == loggedData.FieldId);
            if (field != null)
            {
                Console.WriteLine("Field: " + field.Description);
            }

            foreach(OperationData opData in loggedData.OperationData)
            {
                Console.WriteLine("Operation: " + opData.OperationType);

                switch (opData.OperationType)
                {
                    case OperationTypeEnum.Harvesting:
                        GetHarvestData(opData, catalog);
                        break;
                    case OperationTypeEnum.SowingAndPlanting:
                        GetPlantingData(opData, catalog);
                        break;
                    case OperationTypeEnum.CropProtection:
                    case OperationTypeEnum.Fertilizing:
                        GetAsAppliedData(opData, catalog);
                        break;
                }
            }

            // Clean up
            loggedData.ReleaseSpatialData();

            Console.WriteLine();
        }

        private class RowConfiguration
        {
            // Row unit width in inches
            public double widthIn;

            // Status - determines if the row is active or not
            public WorkingData statusMeter;

            // App Rate - provides application rate information for the current row at any point in time
            public WorkingData appRateMeter;

            // Product index - identifies variety currently being planted
            public WorkingData productIndexMeter;

            public double CalculateAcres(double distanceFt)
            {
                double widthFt = widthIn / 12.0;
                double squareFeet = widthFt * distanceFt;
                double acres = squareFeet / SQUARE_FEET_PER_ACRE;
                return acres;
            }
        }

        private class ProductSummary
        {
            public Product product;
            public double totalAcres;
            public double totalAmount;

            public double averageRatePerAcre
            {
                get
                {
                    if (totalAcres == 0)
                    {
                        return 0;
                    }

                    return totalAmount / totalAcres;
                }
            }

            public override string ToString()
            {
                String summaryTxt = "";
                if (product != null)
                {
                    summaryTxt = "Variety: " + product.Description + "\n";
                }

                summaryTxt += String.Format("\tArea: {0:F2}\tAmount: {1:F2}\tRate: {2:F2}", totalAcres, totalAmount, averageRatePerAcre);

                return summaryTxt;
            }
        }

        private static void GetPlantingData(OperationData opData, Catalog catalog)
        {
            // Get the distance meter from the Machine/Vehicle level 
            WorkingData distanceMeter = null;

            IEnumerable<DeviceElementUse> machineDEUs = opData.GetDeviceElementUses(0);
            foreach (DeviceElementUse deu in machineDEUs)
            {
                IEnumerable<WorkingData> workingDatas = deu.GetWorkingDatas();
                foreach (WorkingData meter in workingDatas)
                {
                    switch (meter.Representation.Code)
                    {
                        case "vrDistanceTraveled":
                            distanceMeter = meter;
                            break;
                    }
                }
            }

            // Get the row-level meters for determining status, seeding rates, and product assignment for each row
            IEnumerable<DeviceElementUse> rowDEUs = opData.GetDeviceElementUses(2);
            List<RowConfiguration> rows = new List<RowConfiguration>();

            NumericWorkingData exampleRowMeter = null;
            foreach (DeviceElementUse deu in rowDEUs)
            {
                // Link to the catalog.DeviceElementConfigurations in order to determine the width of the row.
                //  Row widths are always in inches
                SectionConfiguration rowConfig = catalog.DeviceElementConfigurations.Find(x => x.Id.ReferenceId == deu.DeviceConfigurationId) as SectionConfiguration;
                RowConfiguration row = new RowConfiguration()
                {
                    widthIn = rowConfig.SectionWidth.Value.Value
                };

                IEnumerable<WorkingData> workingDatas = deu.GetWorkingDatas();
                foreach (WorkingData meter in workingDatas)
                {
                    switch (meter.Representation.Code)
                    {
                        case "dtRecordingStatus":
                            row.statusMeter = meter;
                            break;
                        case "vrSeedRateMassActual":
                        case "vrSeedRateSeedsActual":
                            row.appRateMeter = meter;
                            exampleRowMeter = meter as NumericWorkingData;
                            break;
                        case "vrProductIndex":
                            row.productIndexMeter = meter;
                            break;
                    }
                }

                rows.Add(row);
            }

            string rateUnits = "";
            if (exampleRowMeter != null)
            {
                rateUnits = exampleRowMeter.UnitOfMeasure.Code;
            }

            // Initialize the productSummary dictionary
            //  Each product used in the planting operation is identified in the opData.ProductIds list.
            Dictionary<int, ProductSummary> productSummaryByProductId = new Dictionary<int, ProductSummary>();
            foreach(int productId in opData.ProductIds)
            {
                ProductSummary productSummary = new ProductSummary();
                productSummary.product = catalog.Products.Find(x => x.Id.ReferenceId == productId);

                productSummaryByProductId[productId] = productSummary;
            }

            // Keep track of the default productId.
            //  Single-product applications will only specify one product and will not use vrProductIndex meters
            int defaultProductId = opData.ProductIds[0];

            // Loop through all the spatial records
            IEnumerable<SpatialRecord> spatialRecords = opData.GetSpatialRecords();
            foreach (SpatialRecord spatialRecord in spatialRecords)
            {
                NumericRepresentationValue distance = spatialRecord.GetMeterValue(distanceMeter) as NumericRepresentationValue;
                double distanceFt = distance.Value.Value;

                // Loop through each row - we need to examine the status, product assignment, and rate of each row individually
                foreach (RowConfiguration row in rows)
                {
                    EnumeratedValue rowStatus = spatialRecord.GetMeterValue(row.statusMeter) as EnumeratedValue;
                    if (rowStatus.Value.Value == "Off")
                    {
                        // Skip inactive rows
                        continue;
                    }

                    int productId = defaultProductId;
                    if (row.productIndexMeter != null)
                    {
                        // Check to see which product is currently being planted by the current row unit
                        //  Only used for split planter and multi-hybrid planter
                        NumericRepresentationValue productIndex = spatialRecord.GetMeterValue(row.productIndexMeter) as NumericRepresentationValue;
                        if (productIndex != null)
                        {
                            productId = (int)productIndex.Value.Value;
                        }
                    }

                    // Calculate the number of acres covered by this row unit at this point in time
                    double acres = row.CalculateAcres(distanceFt);
                    productSummaryByProductId[productId].totalAcres += acres;

                    if (row.appRateMeter != null)
                    {
                        NumericRepresentationValue appRate = spatialRecord.GetMeterValue(row.appRateMeter) as NumericRepresentationValue;
                        if (appRate != null)
                        {
                            double rate = appRate.Value.Value;    // seeds/ac or lbs/ac
                            double amount = rate * acres;    // seeds or lbs
                            productSummaryByProductId[productId].totalAmount += amount;
                        }
                    }
                }
            }

            Console.WriteLine("Planting Sumamry by Variety.  Rate Units: " + rateUnits);
            foreach(ProductSummary productSummary in productSummaryByProductId.Values)
            {
                Console.WriteLine(productSummary.ToString());
            }
            
        }

        private static double GetActiveWidthFt(SpatialRecord spatialRecord, List<RowConfiguration> rows)
        {
            double activeWidthIn = 0;

            foreach(RowConfiguration row in rows)
            {
                EnumeratedValue rowStatus = spatialRecord.GetMeterValue(row.statusMeter) as EnumeratedValue;
                if(rowStatus.Value.Value == "On")
                {
                    activeWidthIn += row.widthIn;
                }
            }

            return activeWidthIn / 12.0;
        }

        private static void GetAsAppliedData(OperationData opData, Catalog catalog)
        {
            WorkingData areaMeter = null;
            WorkingData appRateMeter = null;
            IEnumerable<DeviceElementUse> headDEUs = opData.GetDeviceElementUses(1);
            foreach (DeviceElementUse deu in headDEUs)
            {
                IEnumerable<WorkingData> workingDatas = deu.GetWorkingDatas();
                foreach (WorkingData meter in workingDatas)
                {
                    switch (meter.Representation.Code)
                    {
                        case "vrDeltaArea":
                            areaMeter = meter;
                            break;
                        case "vrAppRateVolumeActual":
                            appRateMeter = meter;
                            break;
                    }
                }
            }

            double totalArea = 0;
            double totalAmount = 0;

            string rateUnits = "";

            // Loop through all the spatial records to get total area and total amount
            IEnumerable<SpatialRecord> spatialRecords = opData.GetSpatialRecords();
            foreach (SpatialRecord spatialRecord in spatialRecords)
            {
                // Calculate the area
                NumericRepresentationValue area = spatialRecord.GetMeterValue(areaMeter) as NumericRepresentationValue;

                double acres = area.Value.Value;
                totalArea += acres;

                NumericRepresentationValue appRate = spatialRecord.GetMeterValue(appRateMeter) as NumericRepresentationValue;
                rateUnits = appRate.Value.UnitOfMeasure.Code;

                double rate = appRate.Value.Value;    // gal/ac
                double amount = rate * acres;    // gal
                totalAmount += amount;
            }

            double averageRate = totalAmount / totalArea;

            Console.WriteLine("Application Summary:");

            string result = String.Format("Area: {0:F2}\tAmount: {1:F2}\tRate: {2:F2} {3}", totalArea, totalAmount, averageRate, rateUnits);
            Console.WriteLine(result);

            if (opData.ProductIds.Count > 0)
            {
                Console.WriteLine("Product Details:");
                Product product = catalog.Products.Find(x => x.Id.ReferenceId == opData.ProductIds[0]);

                result = String.Format("Product Description: {0}", product.Description);
                Console.WriteLine(result);

                if (product.ProductComponents.Count > 0)
                {
                    Console.WriteLine("Product Components:");

                    // Calculate the total rates for each type of ProductComponent in the MixProduct
                    double volumeTotalRate = 0;
                    double massTotalRate = 0;
                    foreach (ProductComponent component in product.ProductComponents)
                    {
                        if (component.Quantity.Value.UnitOfMeasure.Dimension == UnitOfMeasureDimensionEnum.Volume)
                        {
                            // gal
                            volumeTotalRate += component.Quantity.Value.Value;
                        }
                        else if (component.Quantity.Value.UnitOfMeasure.Dimension == UnitOfMeasureDimensionEnum.Mass)
                        {
                            // lb
                            massTotalRate += component.Quantity.Value.Value;
                        }
                    }

                    // Determine the correct total applied date for the MixProduct
                    double totalRate = volumeTotalRate;
                    if (totalRate == 0)
                    {
                        // The volumeTotalRate will be 0 for dry product mixes
                        totalRate = massTotalRate;
                    }

                    // Calculate the total units of tank mix actually applied to the field.
                    //  This is important to be able to determine the actual proportion of each ProductComponent 
                    double totalUnitsOfMixProduct = totalAmount / volumeTotalRate;

                    foreach (ProductComponent component in product.ProductComponents)
                    {
                        Product componentProduct = catalog.Products.Find(x => x.Id.ReferenceId == component.IngredientId);
                        double componentRate = component.Quantity.Value.Value;

                        // Calculate the total amount of the product component
                        double componentAmount = totalUnitsOfMixProduct * componentRate;
                        double componentActualRate = componentAmount / totalArea;

                        result = String.Format("Component: {0}\tAmount: {1:F2}\tRate: {2:F2} {3}/ac", componentProduct.Description, componentAmount, componentActualRate, component.Quantity.Value.UnitOfMeasure.Code);
                        Console.WriteLine(result);
                    }
                }
            }
        }

        private static void GetHarvestData(OperationData opData, Catalog catalog)
        {
            WorkingData distanceMeter = null;
            WorkingData moistureMeter = null;
            WorkingData wetMassMeter = null;
            WorkingData dryYieldMeter = null;

            IEnumerable<DeviceElementUse> machineDEUs = opData.GetDeviceElementUses(0);
            foreach(DeviceElementUse deu in machineDEUs)
            {
                IEnumerable<WorkingData> workingDatas = deu.GetWorkingDatas();
                foreach(WorkingData meter in workingDatas)
                {
                    switch(meter.Representation.Code)
                    {
                        case "vrDistanceTraveled":
                            distanceMeter = meter;
                            break;
                        case "vrHarvestMoisture":
                            moistureMeter = meter;
                            break;
                        case "vrYieldWetMass":
                            wetMassMeter = meter;
                            break;
                        case "vrYieldVolume":
                            dryYieldMeter = meter;
                            break;
                    }
                }
            }

            // Active width is kept on Level 1 which indicates the header
            WorkingData widthMeter = null;
            IEnumerable<DeviceElementUse> headDEUs = opData.GetDeviceElementUses(1);
            foreach (DeviceElementUse deu in headDEUs)
            {
                IEnumerable<WorkingData> workingDatas = deu.GetWorkingDatas();
                foreach (WorkingData meter in workingDatas)
                {
                    switch (meter.Representation.Code)
                    {
                        case "vrEquipmentWidth":
                            widthMeter = meter;
                            break;
                    }
                }
            }

            double totalArea = 0;
            double totalWetMass = 0;
            double totalDryBushels = 0;
            double totalMoisture = 0;

            // Loop through all the spatial records to get total area, wet mass, and moisture.
            IEnumerable<SpatialRecord> spatialRecords = opData.GetSpatialRecords();
            foreach (SpatialRecord spatialRecord in spatialRecords)
            {
                // Calculate the area
                NumericRepresentationValue distance = spatialRecord.GetMeterValue(distanceMeter) as NumericRepresentationValue;
                NumericRepresentationValue width = spatialRecord.GetMeterValue(widthMeter) as NumericRepresentationValue;

                double distanceFt = distance.Value.Value;
                double widthFt = width.Value.Value;

                double squareFeet = distanceFt * widthFt;
                double acres = squareFeet / SQUARE_FEET_PER_ACRE;
                totalArea += acres;

                NumericRepresentationValue wetMass = spatialRecord.GetMeterValue(wetMassMeter) as NumericRepresentationValue;

                double wetMassLbs = wetMass.Value.Value;    // lbs
                totalWetMass += wetMassLbs;

                NumericRepresentationValue moisture = spatialRecord.GetMeterValue(moistureMeter) as NumericRepresentationValue;

                double moisturePct = moisture.Value.Value;  // %
                totalMoisture += moisturePct * wetMassLbs;  // Only used for calculating weighted average

                NumericRepresentationValue dryVol = spatialRecord.GetMeterValue(dryYieldMeter) as NumericRepresentationValue;

                double dryVolBu = dryVol.Value.Value;    // bu
                totalDryBushels += dryVolBu;
            }

            double averageMoisture = totalMoisture / totalWetMass;

            string result = String.Format("Area: {0:F2}\tWetMass: {1:F2}\tMoisture: {2:F2}\tDryBushels: {3:F2}", totalArea, totalWetMass, averageMoisture, totalDryBushels);
            Console.WriteLine(result);
        }
    }
}
