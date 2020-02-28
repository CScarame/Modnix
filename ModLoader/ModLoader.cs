﻿using Harmony;
using Sheepy.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Text;
using static System.Reflection.BindingFlags;

namespace Sheepy.Modnix {

   public static class ModLoader {
      private readonly static string MOD_PATH  = "My Games/Phoenix Point/Mods".FixSlash();

      internal static Logger Log;
      public static Version LoaderVersion, GameVersion;
      internal readonly static Version PPML_COMPAT = new Version( 0, 1 );

      public static string ModDirectory { get; private set; }

      internal static readonly string[] PHASES = new string[]{ "SplashMod", "Init", "Initialize", "MainMod" };

      #region Initialisation
      private static bool RunMainPhaseOnInit;
      private static object harmony;
      private static Assembly GameAssembly;

      public static void Init () { try {
         if ( Log == null ) { // First run
            RunMainPhaseOnInit = true;
            Setup();
            ModScanner.BuildModList();
            LoadMods( "SplashMod" );
            PatchMenuCrt();
         } else if ( RunMainPhaseOnInit ) { // Subsequence runs
            MainPhase();
            return;
         }
      } catch ( Exception ex ) { 
         if ( Log == null )
            Console.WriteLine( ex );
         else
            Log.Error( ex );
      } }

      private static void PatchMenuCrt () { try {
         var patcher = HarmonyInstance.Create( typeof( ModLoader ).Namespace );
         patcher.Patch( 
            GetGameAssembly().GetType( "PhoenixPoint.Common.Game.PhoenixGame" ).GetMethod( "MenuCrt", NonPublic | Instance ),
            postfix: new HarmonyMethod( typeof( ModLoader ).GetMethod( nameof( MainPhase ), NonPublic | Static ) )
         );
         harmony = patcher;
         RunMainPhaseOnInit = false;
      } catch ( Exception ex ) { Log.Error( ex ); } }

      private static void UnpatchMenuCrt () { try {
         (harmony as HarmonyInstance)?.UnpatchAll( typeof( ModLoader ).Namespace );
         harmony = null;
      } catch ( Exception ex ) { Log.Error( ex ); } }

      private static void MainPhase () {
         RunMainPhaseOnInit = false;
         LoadMods( "Init" ); // PPML v0.1
         LoadMods( "Initialize" ); // PPML v0.2
         LoadMods( "MainMod" ); // Modnix
         if ( harmony != null ) UnpatchMenuCrt();
      }

      private static Assembly GetGameAssembly () {
         if ( GameAssembly != null ) return GameAssembly;
         foreach ( var e in AppDomain.CurrentDomain.GetAssemblies() )
            if ( e.FullName.StartsWith( "Assembly-CSharp, ", StringComparison.InvariantCultureIgnoreCase ) )
               return GameAssembly = e;
         return null;
      }

      public static bool NeedSetup => ModDirectory == null;

      public static void Setup () { try {
         if ( ModDirectory != null ) return;
         // Dynamically load embedded dll
         AppDomain.CurrentDomain.AssemblyResolve += ( domain, dll ) => { try {
            Log.Trace( "Resolving {0}", dll.Name );
            AppDomain app = domain as AppDomain ?? AppDomain.CurrentDomain;
            if ( dll.Name.StartsWith( "PhoenixPointModLoader, Version=0.2.0.0, ", StringComparison.InvariantCultureIgnoreCase ) ) {
               Log.Verbo( "Loading embedded PPML v0.2" );
               return app.Load( Properties.Resources.PPML_0_2 );
            }
            if ( dll.Name.StartsWith( "System." ) && dll.Name.Contains( ',' ) ) { // Generic system library lookup
               string file = dll.Name.Substring( 0, dll.Name.IndexOf( ',' ) ) + ".dll";
               string target = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.Windows ), "Microsoft.NET/Framework/v4.0.30319", file );
               if ( File.Exists( target ) ) {
                  Log.Info( "Loading {0}", target );
                  return Assembly.LoadFrom( target );
               }
            }
            return null;
         } catch ( Exception ) { return null; } };
         ModDirectory = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.MyDocuments ), MOD_PATH );
         if ( Log == null ) {
            if ( ! Directory.Exists( ModDirectory ) )
               Directory.CreateDirectory( ModDirectory );
            SetLog( new FileLogger( Path.Combine( ModDirectory, Assembly.GetExecutingAssembly().GetName().Name + ".log" ) ){ TimeFormat = "HH:mm:ss.ffff " }, true );
            LogGameVersion();
         }
         var corlib = new Uri( typeof( string ).Assembly.CodeBase ).LocalPath;
         Log.Verbo( ".Net/{0}; mscorlib/{1} {2}", Environment.Version, FileVersionInfo.GetVersionInfo( corlib ).FileVersion, corlib );
      } catch ( Exception ex ) { Log?.Error( ex ); } }

      public static void SetLog ( Logger logger, bool clear = false ) {
         if ( Log != null ) throw new InvalidOperationException();
         Log = logger ?? throw new NullReferenceException( nameof( logger ) );
         logger.Filters.Clear();
         logger.Filters.Add( LogFilters.FormatParams );
         logger.Filters.Add( LogFilters.ResolveLazy );
         //logger.Level = SourceLevels.All;
         if ( clear ) Log.Clear();
         LoaderVersion = Assembly.GetExecutingAssembly().GetName().Version;
         Log.Info( "{0}/{1}; {2}", typeof( ModLoader ).FullName, LoaderVersion, DateTime.Now.ToString( "u" ) );
         ModMetaJson.JsonLogger.Masters.Clear();
         ModMetaJson.JsonLogger.Masters.Add( Log );
      }

      public static void LogGameVersion () { try {
         var game = GetGameAssembly();
         if ( game == null ) return;
         var ver = game.GetType( "Base.Build.RuntimeBuildInfo" ).GetProperty( "Version" ).GetValue( null )?.ToString();
         Log.Info( "{0}/{1}", Path.GetFileNameWithoutExtension( game.CodeBase ), ver );
         GameVersion = Version.Parse( ver );
      } catch ( Exception ex ) { Log?.Error( ex ); } }
      #endregion

      #region Loading Mods
      public static void LoadMods ( string phase ) { try {
         Log.Info( "PHASE {0}", phase );
         foreach ( var mod in ModScanner.EnabledMods ) {
            if ( mod.Metadata.Dlls == null ) continue;
            foreach ( var dll in mod.Metadata.Dlls ) {
               if ( dll.Methods == null ) continue;
               if ( ! dll.Methods.TryGetValue( phase, out var entries ) ) continue;
               var lib = LoadDll( dll.Path );
               if ( lib == null ) continue;
               foreach ( var type in entries )
                  CallInit( mod, lib, type, phase );
            }
         }
         Log.Flush();
      } catch ( Exception ex ) { Log.Error( ex ); } }

      public static Assembly LoadDll ( string path ) { try {
         Log.Info( "Loading {0}", path );
         return Assembly.LoadFrom( path );
      } catch ( Exception ex ) { Log.Error( ex ); return null; } }

      private static Action<object> LoggerA ( Logger log ) => ( msg ) =>
         log.Log( msg is Exception ? SourceLevels.Error : SourceLevels.Information, msg, null );
      private static Action<object,object[]> LoggerB ( Logger log ) => ( msg, augs ) =>
         log.Log( msg is Exception ? SourceLevels.Error : SourceLevels.Information, msg, augs );
      private static Action<SourceLevels,object> LoggerC ( Logger log ) => ( lv, msg ) => log.Log( lv, msg, null );
      private static Action<SourceLevels,object,object[]> LoggerD ( Logger log ) => ( lv, msg, augs ) => log.Log( lv, msg, augs );

      public static void CallInit ( ModEntry mod, Assembly dll, string typeName, string methodName ) { try {
         var type = dll.GetType( typeName );
         if ( type == null ) {
            Log.Error( "Cannot find type {1} in {0}", dll.Location, typeName );
            return;
         }

         var func = type.GetMethod( methodName, ModScanner.INIT_METHOD_FLAGS );
         if ( func == null ) {
            Log.Error( "Cannot find {1}.{2} in {0}", dll.Location, typeName, methodName );
            return;
         }
         var augs = new List<object>();
         foreach ( var aug in func.GetParameters() ) {
            var pType = aug.ParameterType;
            // Version checkers
            if ( pType == typeof( Func<string,Version> ) )
               augs.Add( (Func<string,Version>) ModScanner.GetVersionById );
            else if ( pType == typeof( Assembly ) )
               augs.Add( Assembly.GetExecutingAssembly() );
            // Loggers
            else if ( pType == typeof( Action<object> ) && aug.Name.ToLowerInvariant().Contains( "log" ) )
               augs.Add( LoggerA( CreateLogger( mod ) ) );
            else if ( pType == typeof( Action<object,object[]> ) && aug.Name.ToLowerInvariant().Contains( "log" ) )
               augs.Add( LoggerB( CreateLogger( mod ) ) );
            else if ( pType == typeof( Action<SourceLevels,object> ) && aug.Name.ToLowerInvariant().Contains( "log" ) )
               augs.Add( LoggerC( CreateLogger( mod ) ) );
            else if ( pType == typeof( Action<SourceLevels,object,object[]> ) && aug.Name.ToLowerInvariant().Contains( "log" ) )
               augs.Add( LoggerD( CreateLogger( mod ) ) );
            // Mod info
            else if ( pType == typeof( Func<string,ModEntry> ) )
               augs.Add( (Func<string,ModEntry>) ModScanner.GetModById );
            else if ( pType == typeof( ModMeta ) )
               augs.Add( mod.Metadata );
            else if ( pType == typeof( ModEntry ) )
               augs.Add( mod );
            // Defaults
            else if ( pType.IsValueType )
               augs.Add( Activator.CreateInstance( pType ) );
            else
               augs.Add( null );
         }
         Func<string> augTxt = () => string.Join( ", ", augs.Select( e => e?.GetType()?.Name ?? "null" ) );
         Log.Info( "Calling {1}.{2}({3}) in {0}", mod.Path, typeName, methodName, augTxt );
         object target = null;
         if ( ! func.IsStatic ) {
            if ( mod.Instance == null )
               mod.Instance = Activator.CreateInstance( type );
            target = mod.Instance;
         }
         func.Invoke( target, augs.ToArray() );
      } catch ( Exception ex ) { Log.Error( ex ); } }

      private static Logger CreateLogger ( ModEntry mod ) { lock ( mod ) {
         if ( mod.Logger == null ) {
            var logger = mod.Logger = new LoggerProxy( Log );
            var filters = logger.Filters;
            filters.Add( LogFilters.IgnoreDuplicateExceptions );
            filters.Add( LogFilters.AutoMultiParam );
            filters.Add( LogFilters.AddPrefix( mod.Metadata.Id + "┊" ) );
         }
         return mod.Logger;
      } }
      #endregion
   }

   internal static class Tools {
      internal static string FixSlash ( this string path ) => path.Replace( '/', Path.DirectorySeparatorChar );
   }
}