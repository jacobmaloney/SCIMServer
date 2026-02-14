using System;
using System.IO;
using System.Threading.Tasks;
using SCIMServer.Installer;
using SCIMServer.Installer.Steps;

namespace SCIMServer.Installer
{
    /// <summary>
    /// Main installer program for SCIM Server
    /// </summary>
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.Title = "SCIM Server Installer";
            
            var installer = new InstallerWizard();
            
            // Register installation steps
            installer.AddStep(new WelcomeStep());
            installer.AddStep(new LicenseStep());
            installer.AddStep(new InstallationTypeStep());
            installer.AddStep(new DatabaseConfigurationStep());
            installer.AddStep(new ServerConfigurationStep());
            installer.AddStep(new SecurityConfigurationStep());
            installer.AddStep(new InstallationStep());
            installer.AddStep(new CompletionStep());

            // Run the installer
            try
            {
                await installer.RunAsync();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nInstallation failed: {ex.Message}");
                Console.ResetColor();
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                Environment.Exit(1);
            }
        }
    }
}
