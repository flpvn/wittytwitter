using System;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Windows;
using TwitterLib;
using log4net;
using log4net.Config;
namespace Witty
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>

    public partial class App : System.Windows.Application
    {
        private static readonly ILog logger = LogManager.GetLogger("Witty.Logging");

        // Global variable for the user
        public static User LoggedInUser = null;

        protected override void OnStartup(StartupEventArgs e)
        {
            DOMConfigurator.Configure();

            logger.Info("Witty is starting.");

            Properties.Settings appSettings = Witty.Properties.Settings.Default;
            if (appSettings.UpgradeSettings)
            {
                Witty.Properties.Settings.Default.Upgrade();
                appSettings.UpgradeSettings = false;
            }

            if (!string.IsNullOrEmpty(appSettings.Skin))
            {
                try
                {
                    ResourceDictionary rd = new ResourceDictionary();
                    rd.MergedDictionaries.Add(Application.LoadComponent(new Uri(appSettings.Skin, UriKind.Relative)) as ResourceDictionary);
                    Application.Current.Resources = rd;
                }
                catch
                {
                    logger.Error("Selected skin not found");
                    // REVIEW: Should witty do something smart here?
                }
            }

            base.OnStartup(e);

            logger.Info("Witty has completed startup.");
        }

        /// <summary>
        /// Gets the collection of skins
        /// </summary>
        public static NameValueCollection Skins
        {
            get
            {
                NameValueCollection skins = new NameValueCollection();

                foreach (string folder in Directory.GetDirectories(@".\"))
                {
                    foreach (string file in Directory.GetFiles(folder))
                    {
                        FileInfo fileInfo = new FileInfo(file);
                        if (string.Compare(fileInfo.Extension, ".xaml", true, CultureInfo.InvariantCulture) == 0)
                        {
                            // Use the first part of the resource file name for the menu item name.
                            //skins.Add(fileInfo.Name.Remove(fileInfo.Name.IndexOf(".xaml")),
                            //    Path.Combine(folder, fileInfo.Name));

                            skins.Add(Path.Combine(folder, fileInfo.Name), Path.Combine(folder, fileInfo.Name));
                        }
                    }
                }
                return skins;
            }
        }
    }
}