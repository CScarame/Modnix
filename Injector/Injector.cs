﻿using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Options;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using static System.Console;

// TODO: PPML inject check and restore is broken after changing injection point.
// Need to refactor the whole thing.
namespace Sheepy.Modnix {
   internal static class Injector {
      // return codes
      private const int RC_NORMAL = 0;
      private const int RC_UNHANDLED_STATE = 1;
      private const int RC_BAD_OPTIONS = 2;
      private const int RC_MISSING_BACKUP_FILE = 3;
      private const int RC_BACKUP_FILE_INJECTED = 4;
      private const int RC_BAD_MANAGED_DIRECTORY_PROVIDED = 5;
      private const int RC_MISSING_MOD_LOADER_ASSEMBLY = 6;
      private const int RC_REQUIRED_GAME_VERSION_MISMATCH = 7;

      private const string MOD_LOADER_NAME = "Modnix";
      private const string MOD_INJECTOR_EXE_FILE_NAME = "ModnixInjector.exe";
      private const string MOD_LOADER_DLL_FILE_NAME = "ModnixLoader.dll";
      private const string INJECTED_DLL_FILE_NAME = "Cinemachine.dll";
      private const string GAME_DLL_FILE_NAME = "Assembly-CSharp.dll";
      private const string BACKUP_FILE_EXT = ".orig";

      // PPML Late injection goes here
      //private const string HOOK_TYPE     = "PhoenixPoint.Common.Game.PhoenixGame";
      //private const string HOOK_METHOD   = "BootCrt";
      private const string HOOK_TYPE     = "Cinemachine.CinemachineBrain";
      private const string HOOK_METHOD   = "OnEnable";
      private const string INJECT_TYPE   = "Sheepy.Modnix.ModLoader";
      private const string INJECT_METHOD = "Init";
      //private const string INJECT_CALL   = "MenuCrt";

      private const string PPML_INJECTOR_EXE    = "PhoenixPointModLoaderInjector.exe";
      private const string PPML_INJECTOR_TYPE   = "PhoenixPointModLoader.PPModLoader";
      private const string PPML_INJECTOR_METHOD = "Init";

      private const string GAME_VERSION_TYPE   = "Base.Build.RuntimeBuildInfo";
      private const string GAME_VERSION_METHOD = "get_Version";

      private static readonly AppState State = new AppState();
      private static readonly ReceivedOptions OptionsIn = new ReceivedOptions();
      private static readonly OptionSet Options = new OptionSet {
         {
            "d|detect", "Detect game assembly's injection state: none, modnix, ppml",
            v => OptionsIn.Detecting = v != null
         },
         {
            "g|gameversion", "Print game version",
            v => OptionsIn.GameVersion = v != null
         },
         {
            "h|?|help", "Print this useful help message",
            v => OptionsIn.Helping = v != null
         },
         {
            "i|install", "Install the Mod (this is the default behavior)",
            v => OptionsIn.Installing = v != null
         },
         {
            "manageddir=", "Specify managed dir where game's Assembly-CSharp.dll is located",
            v => OptionsIn.ManagedDir = v
         },
         {
            "y|nokeypress", "Anwser prompts affirmatively",
            v => OptionsIn.RequireKeyPress = v == null
         },
         {
            "requiredversion=", "Don't continue with /install, /restore, etc. if game version does not match given argument",
            v => OptionsIn.RequiredGameVersion = v
         },
         {
            "r|restore", "Restore pristine backup game assembly to folder",
            v => OptionsIn.Restoring = v != null
         },
         {
            "v|version", "Print injector version number",
            v => OptionsIn.Versioning = v != null
         }
      };

      private static int Main ( string[] args ) {
         try {
            ParseOptions( args );
            LoadGameAssembly();

            SayHeader();

            if ( OptionsIn.Restoring ) {
               if ( State.targetDllInjected > InjectionState.NONE )
                  Restore( State.targetDllPath, State.targetDllBackupPath );
               else
                  SayAlreadyRestored();
               return PromptForKey( OptionsIn.RequireKeyPress );
            }

            if ( OptionsIn.Installing ) {
               bool injected = false;
               try {
                  if ( State.targetDllInjected == InjectionState.NONE ) {
                     Backup( State.targetDllPath, State.targetDllBackupPath );
                     injected = Inject( State.targetDllPath, State.modLoaderDllPath );
                  } else if ( State.targetDllInjected == InjectionState.PPML ) {
                     SayPpmlMigrate();
                     Restore( State.targetDllPath, State.targetDllBackupPath );
                     injected = Inject( State.targetDllPath, State.modLoaderDllPath );
                  } else if ( State.targetDllInjected == InjectionState.MODNIX ) {
                     SayAlreadyInjected();
                     injected = true;
                  } else
                     throw new InvalidOperationException();
               } catch ( Exception ex ) {
                  SayException( ex );
               }
               if ( injected )
                  SayPpmlWarning();
               return PromptForKey( OptionsIn.RequireKeyPress );
            }

         } catch ( BackupFileError e ) {
            SayException( e );
            return SayHowToRecoverMissingBackup( e.BackupFileName );

         } catch ( Exception e ) {
            SayException( e );
         }

         return RC_UNHANDLED_STATE;
      }

      private static void Exit ( int exitCode ) => Environment.Exit( exitCode );

      private static void ParseOptions ( string[] args ) {
         try {
            Options.Parse( args );
         } catch ( OptionException e ) {
            Exit( SayOptionException( e ) );
         }

         if ( OptionsIn.Helping )
            Exit( SayHelp( Options ) );

         if ( OptionsIn.Versioning )
            Exit( SayVersion() );
      }

      private static void LoadGameAssembly () {
         if ( ! string.IsNullOrEmpty( OptionsIn.ManagedDir ) ) {
            if ( ! Directory.Exists( OptionsIn.ManagedDir ) )
               Exit( SayManagedDirMissingError( OptionsIn.ManagedDir ) );
            State.managedDirectory = Path.GetFullPath( OptionsIn.ManagedDir );
         } else
            State.managedDirectory = Directory.GetCurrentDirectory();

         State.gameDllPath         = Path.Combine( State.managedDirectory, GAME_DLL_FILE_NAME );
         State.targetDllPath       = Path.Combine( State.managedDirectory, INJECTED_DLL_FILE_NAME );
         State.targetDllBackupPath = Path.Combine( State.managedDirectory, INJECTED_DLL_FILE_NAME + BACKUP_FILE_EXT );
         State.modLoaderDllPath    = Path.Combine( State.managedDirectory, MOD_LOADER_DLL_FILE_NAME );
         State.ppmlInjectorPath    = Path.Combine( State.managedDirectory, PPML_INJECTOR_EXE );

         if ( ! File.Exists( State.targetDllPath ) )
            Exit( SayGameAssemblyMissingError( OptionsIn.ManagedDir ) );

         if ( ! File.Exists( State.modLoaderDllPath ) )
            Exit( SayModLoaderAssemblyMissingError( State.modLoaderDllPath ) );

         State.gameVersion = LoadVersion( State.gameDllPath );
         if ( OptionsIn.GameVersion )
            Exit( SayGameVersion( State.gameVersion ) );

         if ( ! string.IsNullOrEmpty( OptionsIn.RequiredGameVersion ) && OptionsIn.RequiredGameVersion != State.gameVersion ) {
            SayRequiredGameVersionMismatchMessage( State.gameVersion, OptionsIn.RequiredGameVersion );
            Exit( PromptForKey( OptionsIn.RequireKeyPress, RC_REQUIRED_GAME_VERSION_MISMATCH ) );
         }

         State.targetDllInjected = CheckInjection( State.targetDllPath );
         if ( OptionsIn.Detecting )
            Exit( SayInjectedStatus( State.targetDllInjected ) );
      }

      private static int SayInjectedStatus ( InjectionState injected ) {
         WriteLine( injected.ToString().ToLower() );
         return RC_NORMAL;
      }

      private static void Backup ( string filePath, string backupFilePath ) {
         File.Copy( filePath, backupFilePath, true );
         WriteLine( $"{Path.GetFileName( filePath )} backed up to {Path.GetFileName( backupFilePath )}" );
      }

      private static void Restore ( string filePath, string backupFilePath ) {
         if ( ! File.Exists( backupFilePath ) )
            throw new BackupFileNotFound();

         if ( CheckInjection( backupFilePath ) > InjectionState.NONE )
            throw new BackupFileInjected();

         File.Copy( backupFilePath, filePath, true );
         WriteLine( $"{Path.GetFileName( backupFilePath )} restored to {Path.GetFileName( filePath )}" );
      }

      private static bool Inject ( string hookFilePath, string injectFilePath ) {
         WriteLine( $"Injecting {Path.GetFileName( hookFilePath )} with {INJECT_TYPE}.{INJECT_METHOD} at {HOOK_TYPE}.{HOOK_METHOD}" );
         using ( var game = ModuleDefinition.ReadModule( hookFilePath, new ReaderParameters { ReadWrite = true } ) )
         using ( var injecting = ModuleDefinition.ReadModule( injectFilePath ) ) {
            var success = InjectModHookPoint( game, injecting );
            if ( success )
               success &= WriteNewAssembly( hookFilePath, game );
            if ( !success )
               WriteLine( "Failed to inject the game assembly." );
            return success;
         }
      }

      private static bool WriteNewAssembly ( string hookFilePath, ModuleDefinition game ) {
         // save the modified assembly
         WriteLine( $"Writing back to {Path.GetFileName( hookFilePath )}..." );
         game.Write();
         WriteLine( "Injection complete!" );
         return true;
      }

      private static bool InjectModHookPoint ( ModuleDefinition game, ModuleDefinition injecting ) {
         // get the methods that we're hooking and injecting
         var injectedMethod = injecting.GetType( INJECT_TYPE ).Methods.Single( x => x.Name == INJECT_METHOD );
         var hookedMethod = game.GetType( HOOK_TYPE ).Methods.First( x => x.Name == HOOK_METHOD );
         /*
         PPML injection code for late-injection (after logos and game splash) 

         // Since the return type is an iterator -- need to go searching for its MoveNext method which contains the actual code you'll want to inject
         if ( hookedMethod.ReturnType.Name.Contains( "IEnumerator" ) ) {
            var nestedIterator = game.GetType( HOOK_TYPE ).NestedTypes.First( x => x.Name.Contains( HOOK_METHOD ) );
            hookedMethod = nestedIterator.Methods.First( x => x.Name.Equals( "MoveNext" ) );
         }

         // As of Phoenix Point v1.0.54973 the BootCrt() iterator method of PhoenixGame has this at the end
         //
         //  ...
         //    IEnumerator<NextUpdate> coroutine = this.MenuCrt(null, MenuEnterReason.None);
         //    yield return timing.Call(coroutine, null);
         //    yield break;
         //  }
         //
         // We want to inject after the MenuCrt call -- so search for that call in the CIL

         WriteLine( $"Scanning for {INJECT_CALL} call in {HOOK_METHOD}." );

         var body = hookedMethod.Body;
         var code = body.Instructions;
         WriteLine( $"Total {code.Count} IL instructions." );
         for ( var i = code.Count - 1 ; i >= 0 ; i-- ) {
            var instruction = code[i];
            var opcode = instruction.OpCode;
            if ( ! opcode.Code.Equals( Code.Call ) ) continue;
            if ( ! opcode.OperandType.Equals( OperandType.InlineMethod ) ) continue;
            var methodReference = instruction.Operand as MethodReference;
            if ( methodReference != null && methodReference.Name.Contains( INJECT_CALL ) ) {
               WriteLine( $"Call found. Inserting mod loader after call." );
               body.GetILProcessor().InsertAfter( instruction, Instruction.Create( OpCodes.Call, game.ImportReference( injectedMethod ) ) );
               WriteLine( "Insertion done." );
               return true;
            }
         }
         */

         var body = hookedMethod.Body;
         var len = body.Instructions.Count;
         var target = body.Instructions[ len - 1 ];
         WriteLine( $"Found {len} IL instructions in {HOOK_METHOD}." );
         if ( target.OpCode.Code.Equals( Code.Ret ) ) {
            WriteLine( $"Injecting before last op {target}" );
            body.GetILProcessor().InsertBefore( target, Instruction.Create( OpCodes.Call, game.ImportReference( injectedMethod ) ) );
            return true;
         }

         WriteLine( $"Injection mark not found. Found {target.OpCode} instead." );
         return false;
      }

      private static string LoadVersion ( string dllPath ) {
         using ( var dll = ModuleDefinition.ReadModule( dllPath ) ) {
            foreach ( var type in dll.Types ) {
               if ( type.FullName == GAME_VERSION_TYPE )
                  return FindGameVersion( type );
            }
         }
         return null;
      }

      private static readonly string ModnixInjextCheck = $"System.Void {INJECT_TYPE}::{INJECT_METHOD}(";
      private static readonly string PPMLInjextCheck = $"System.Void {INJECT_TYPE}::{INJECT_METHOD}(";

      private static InjectionState CheckInjection ( string dllPath ) {
         using ( var dll = ModuleDefinition.ReadModule( dllPath ) ) {
            foreach ( var type in dll.Types ) {
               var result = CheckInjection( type );
               if ( result != InjectionState.NONE ) return result;
            }
         }
         return InjectionState.NONE;
      }

      private static InjectionState CheckInjection ( TypeDefinition typeDefinition ) {
         // Check standard methods, then in places like IEnumerator generated methods (Nested)
         var result = typeDefinition.Methods.Select( CheckInjection ).FirstOrDefault( e => e != InjectionState.NONE );
         if ( result != InjectionState.NONE ) return result;
         return typeDefinition.NestedTypes.Select( CheckInjection ).FirstOrDefault( e => e != InjectionState.NONE );
      }

      private static InjectionState CheckInjection ( MethodDefinition methodDefinition ) {
         if ( methodDefinition.Body == null )
            return InjectionState.NONE;
         foreach ( var instruction in methodDefinition.Body.Instructions ) {
            if ( ! instruction.OpCode.Equals( OpCodes.Call ) ) continue;
            string op = instruction.Operand.ToString();
            if ( op.StartsWith( ModnixInjextCheck ) )
               // Update check:
               // if ( methodDefinition.FullName.Contains( HOOK_TYPE ) && methodDefinition.FullName.Contains( HOOK_METHOD ) )
               return InjectionState.MODNIX;
            else if ( op.StartsWith( PPMLInjextCheck ) )
               return InjectionState.PPML;
         }
         return InjectionState.NONE;
      }

      private static string FindGameVersion ( TypeDefinition type ) {
         var method = type.Methods.FirstOrDefault( e => e.Name == "get_Version" );
         if ( method == null || ! method.HasBody ) return "ERR version not found";

         try {
            int[] version = new int[2];
            int ldcCount = 0;
            foreach ( var code in method.Body.Instructions ) {
               string op = code.OpCode.ToString();
               if ( ! op.StartsWith( "ldc.i4" ) ) continue;
               if ( ldcCount >= 2 ) return "ERR too many vers";

               int ver = 0;
               if ( code.Operand is int num ) ver = num;
               else if ( code.Operand is sbyte num2 ) ver = num2;
               else if ( code.OpCode.Code.Equals( Code.Ldc_I4_M1 ) ) ver = -1;
               else ver = int.Parse( op.Substring( 7 ) );

               version[ ldcCount ] = ver;
               ++ldcCount;
            }
            if ( ldcCount < 2 ) return "ERR too few vers";
            return version[0].ToString() + '.' + version[1];
         } catch ( Exception e ) {
            return $"ERR {e}";
         }
      }

      #region Console output
      private static int SayOptionException ( OptionException e ) {
         SayHeader();
         Write( $"{MOD_INJECTOR_EXE_FILE_NAME}: {e.Message}" );
         WriteLine( $"Try '{MOD_INJECTOR_EXE_FILE_NAME} --help' for more information." );
         return RC_BAD_OPTIONS;
      }

      private static int SayHelp ( OptionSet p ) {
         SayHeader();
         WriteLine( $"Usage: {MOD_INJECTOR_EXE_FILE_NAME} [OPTIONS]+" );
         WriteLine( "Inject the Phoenix Point game assembly with an entry point for mod loading." );
         WriteLine( "If no options are specified, the program assumes you want to /install." );
         WriteLine();
         WriteLine( "Options:" );
         p.WriteOptionDescriptions( Out );
         return RC_NORMAL;
      }

      private static int SayVersion () {
         WriteLine( GetProductVersion() );
         return RC_NORMAL;
      }

      private static int SayManagedDirMissingError ( string givenManagedDir ) {
         SayHeader();
         WriteLine( $"ERROR: We could not find the directory '{givenManagedDir}'. Are you sure it exists?" );
         return RC_BAD_MANAGED_DIRECTORY_PROVIDED;
      }

      private static int SayGameAssemblyMissingError ( string givenManagedDir ) {
         SayHeader();
         WriteLine( $"ERROR: We could not find target assembly {INJECTED_DLL_FILE_NAME} in directory '{givenManagedDir}'.\n" +
             "Are you sure that is the correct directory?" );
         return RC_BAD_MANAGED_DIRECTORY_PROVIDED;
      }

      private static int SayModLoaderAssemblyMissingError ( string expectedModLoaderAssemblyPath ) {
         SayHeader();
         WriteLine( $"ERROR: We could not find the loader assembly {MOD_LOADER_DLL_FILE_NAME} at '{expectedModLoaderAssemblyPath}'.\n" +
             $"Is {MOD_LOADER_DLL_FILE_NAME} in the correct place?  It should be in the same directory as this injector executable." );
         return RC_MISSING_MOD_LOADER_ASSEMBLY;
      }

      private static int SayGameVersion ( string version ) {
         WriteLine( version );
         return RC_NORMAL;
      }

      private static void SayRequiredGameVersionMismatchMessage ( string version, string expectedVersion ) {
         WriteLine( $"Expected game v{expectedVersion}" );
         WriteLine( $"Actual game v{version}" );
         WriteLine( "Game version mismatch" );
      }

      private static string GetProductVersion () {
         var assembly = Assembly.GetExecutingAssembly();
         var fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
         return fvi.FileVersion;
      }

      private static void SayHeader () {
         WriteLine( $"{MOD_LOADER_NAME} Injector {GetProductVersion()}" );
         WriteLine( "----------------------------" );
      }

      private static int SayHowToRecoverMissingBackup ( string backupFileName ) {
         WriteLine( "----------------------------" );
         WriteLine( $"The backup game assembly file must be in the directory with the injector for /restore to work.  The backup file should be named \"{backupFileName}\"." );
         WriteLine( "You may need to reinstall or use Steam/GOG's file verification function if you have no other backup." );
         return RC_MISSING_BACKUP_FILE;
      }

      private static void SayHowToRecoverInjectedBackup ( string backupFileName ) {
         WriteLine( "----------------------------" );
         WriteLine( $"The backup game assembly file named \"{backupFileName}\" was already injected.  Something has gone wrong." );
         WriteLine( "You may need to reinstall or use Steam/GOG's file verification function if you have no other backup." );
      }

      private static void SayPpmlMigrate () => WriteLine( $"{INJECTED_DLL_FILE_NAME} already injected by PPML.  Reverting the file and migrate to Modnix." );

      private static void SayAlreadyInjected () => WriteLine( $"{INJECTED_DLL_FILE_NAME} already injected with {INJECT_TYPE}.{INJECT_METHOD}." );

      private static void SayAlreadyRestored () => WriteLine( $"{INJECTED_DLL_FILE_NAME} already clean.  No injection to revert." );

      private static void SayException ( Exception e ) => WriteLine( $"ERROR: An exception occured: {e}" );

      private static void SayPpmlWarning () {
         if ( File.Exists( State.ppmlInjectorPath ) )
            WriteLine( $"!!! {PPML_INJECTOR_EXE} found.  Deletion is advised as PPML is incompaible with Modnix. !!!" );
      }

      private static int PromptForKey ( bool requireKeyPress, int returnCode = RC_NORMAL ) {
         if ( requireKeyPress ) {
            WriteLine( "Press any key to continue." );
            ReadKey();
         }
         return returnCode;
      }
      #endregion
   }

   public class BackupFileError : Exception {
      public BackupFileError ( string backupFileName, string message ) : base( message ) { BackupFileName = backupFileName; }
      public readonly string BackupFileName;
   }

   public class BackupFileInjected : BackupFileError {
      public BackupFileInjected ( string backupFileName = "Assembly-CSharp.dll.orig" ) :
         base( backupFileName, $"The backup file \"{backupFileName}\" was injected." ) { }
   }

   public class BackupFileNotFound : BackupFileError {
      public BackupFileNotFound ( string backupFileName = "Assembly-CSharp.dll.orig" ) :
         base( backupFileName, $"The backup file \"{backupFileName}\" could not be found." ) { }
   }

   // Values passed by the user into the program via command line.
   internal class ReceivedOptions {
      public bool RequireKeyPress = true;
      public bool Detecting = false;
      public string RequiredGameVersion = string.Empty;
      public string ManagedDir = string.Empty;
      public bool GameVersion = false;
      public bool Helping = false;
      public bool Installing = true;
      public bool Restoring = false;
      public bool Versioning = false;
   }

   internal class AppState {
      internal string managedDirectory;
      internal string gameDllPath;
      internal string targetDllPath;
      internal string targetDllBackupPath;
      internal string gameVersion;
      internal string modLoaderDllPath;
      internal string ppmlInjectorPath;
      internal InjectionState targetDllInjected;
   }

   internal enum InjectionState { NONE = 0, MODNIX = 1, PPML = 2 }
}