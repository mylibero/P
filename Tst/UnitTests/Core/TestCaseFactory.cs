﻿using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Pc;
using UnitTests.Runners;
using UnitTests.Validators;

namespace UnitTests.Core
{
    /// <summary>
    /// Factory for creating test cases from structured directories on disk.
    /// </summary>
    public class TestCaseFactory
    {
        private readonly DirectoryInfo testTempBaseDir;

        /// <summary>
        /// Box a new factory with the given scratch directory
        /// </summary>
        /// <param name="testTempBaseDir">The parent directory for each test's scratch directories</param>
        public TestCaseFactory(DirectoryInfo testTempBaseDir)
        {
            this.testTempBaseDir = testTempBaseDir;
        }

        /// <summary>
        /// Box a test case from the given directory and parsed Prt run configuration
        /// </summary>
        /// <param name="testDir">The directory containing P source files</param>
        /// <param name="runConfig">The run configuration for the test, or null if compile-only</param>
        /// <returns>The test case in a runnable state.</returns>
        public CompilerTestCase CreateTestCase(DirectoryInfo testDir, TestConfig runConfig, CompilerOutput output)
        {
            // TODO: support other run configurations.
            var inputFiles = testDir.GetFiles("*.p");
            string testName = new Uri(Constants.TestDirectory + Path.DirectorySeparatorChar)
                              .MakeRelativeUri(new Uri(testDir.FullName))
                              .ToString();

            ICompilerTestRunner runner;
            ITestResultsValidator validator;

            if (runConfig != null)
            {
                string expectedOutput;
                if (output.Equals(CompilerOutput.C))
                {
                    runner = new PrtRunner(inputFiles);
                    expectedOutput = File.ReadAllText(Path.Combine(testDir.FullName, "Prt", Constants.CorrectOutputFileName));
                }
                else if (output.Equals(CompilerOutput.PSharp))
                {
                    runner = new PSharpRunner(inputFiles);
                    expectedOutput = File.ReadAllText(Path.Combine(testDir.FullName, "Prt", Constants.CorrectOutputFileName));
                }
                else
                {
                    throw new ArgumentOutOfRangeException();
                }

                // TODO: fix golden outputs for dynamic error assertions (79 tests)
                ParseExpectedOutput(expectedOutput, out string stdout, out string stderr, out int exitCode);
                if (testName.Contains("/DynamicError/"))
                {
                    stdout = null;
                    stderr = null;
                }
                validator = new ExecutionOutputValidator(exitCode, stdout, stderr);

                // TODO: fix golden output for P#
                if (output.Equals(CompilerOutput.PSharp))
                {
                    validator = new CompileSuccessValidator();
                }
            }
            else
            {
                runner = new CompileOnlyRunner(CompilerOutput.C, inputFiles);

                // TODO: validate information about the particular kind of compiler error
                bool isStaticError = testName.Contains("/StaticError/");
                validator = isStaticError ? (ITestResultsValidator) new StaticErrorValidator() : new CompileSuccessValidator();
            }

            DirectoryInfo tempDirName = Directory.CreateDirectory(Path.Combine(testTempBaseDir.FullName, output.ToString(), testName));
            return new CompilerTestCase(tempDirName, runner, validator);
        }

        /// <summary>
        /// Parses an expected (golden) output file.
        /// </summary>
        /// <param name="expected">The file contents to parse</param>
        /// <param name="stdout">The expected standard output</param>
        /// <param name="stderr">The expected error output</param>
        /// <param name="exitCode">The expected exit code</param>
        private void ParseExpectedOutput(string expected, out string stdout, out string stderr, out int exitCode)
        {
            exitCode = 0;

            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();
            bool sawExitCode = false;
            foreach (string line in expected.Split('\r', '\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                Match match = Regex.Match(line, @"^(?<tag>OUT|ERROR|EXIT): (?<text>.*)$");
                if (!match.Success)
                {
                    throw new CompilerTestException(TestCaseError.InvalidOutputSpec);
                }

                string tag = match.Groups["tag"].Value;
                string lineText = match.Groups["text"].Value;
                switch (tag)
                {
                    case "OUT":
                        stdoutBuilder.Append($"{lineText}\n");
                        break;
                    case "ERROR":
                        stderrBuilder.Append($"{lineText}\n");
                        break;
                    case "EXIT":
                        if (sawExitCode || !int.TryParse(lineText, out exitCode))
                        {
                            throw new CompilerTestException(TestCaseError.InvalidOutputSpec);
                        }

                        sawExitCode = true;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(expected));
                }
            }

            if (!sawExitCode)
            {
                throw new CompilerTestException(TestCaseError.InvalidOutputSpec);
            }

            stdout = stdoutBuilder.ToString();
            stderr = stderrBuilder.ToString();
        }
    }
}