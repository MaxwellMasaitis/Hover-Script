using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace IngameScript
{
    partial class Program : MyGridProgram
    {
        // This file contains your actual script.
        //
        // You can either keep all your code here, or you can create separate
        // code files to make your program easier to navigate while coding.
        //
        // In order to add a new utility class, right-click on your project, 
        // select 'New' then 'Add Item...'. Now find the 'Space Engineers'
        // category under 'Visual C# Items' on the left hand side, and select
        // 'Utility Class' in the main area. Name it in the box below, and
        // press OK. This utility class will be merged in with your code when
        // deploying your final script.
        //
        // You can also simply create a new utility class manually, you don't
        // have to use the template if you don't want to. Just do so the first
        // time to see what a utility class looks like.
        // 
        // Go to:
        // https://github.com/malware-dev/MDK-SE/wiki/Quick-Introduction-to-Space-Engineers-Ingame-Scripts
        //
        // to learn more about ingame scripts.

        // TODO:
        // possible option: set height relative to planet center rather than natural surface

        IMyShipController controller;
        List<IMyThrust> thrusters = new List<IMyThrust>();
        List<IMyThrust> unmaxedThrusters = new List<IMyThrust>();

        const double dashpot = 600, spring = 10;
        const float minimumThrust = 0.01f;
        const double defaultHeight = 10;
        float thrustPercentage;

        PID _pid;
        const double TimeStep = 1.0 / 60;

        double shipElevation = 0;
        double previousElevation = 0, gravity, T2W, shipMass, shipDisplacement, offGroundVelocity, previousVelocity = 0, offGroundAcceleration, previousAcceleration = 0, offGroundJerk, J_PID;

        Dictionary<string, Action> _commands = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);
        MyCommandLine _commandLine = new MyCommandLine();

        bool hoverMode = false, manual = false;
        double levitationHeight = defaultHeight;

        public Program()
        {
            _pid = new PID(3, 2, 1, TimeStep);

            // Use quotations around negative number values for ModHeight
            _commands["setHeight"] = SetHeight;
            _commands["modHeight"] = ModHeight;
            _commands["toggleHover"] = ToggleHover;
            _commands["manualMode"] = ManualMode;
            _commands["resetHeight"] = ResetHeight;

            Runtime.UpdateFrequency = UpdateFrequency.Update1;
            List<IMyShipController> tempList = new List<IMyShipController>();
            GridTerminalSystem.GetBlocksOfType(tempList, controller => controller.IsSameConstructAs(Me) && controller.IsMainCockpit);
            controller = tempList.First();

            GridTerminalSystem.GetBlocksOfType(thrusters, thruster => (thruster.Orientation.TransformDirection(Base6Directions.Direction.Forward) == controller.Orientation.TransformDirection(Base6Directions.Direction.Down) && thruster.IsSameConstructAs(Me)));

            string[] storedData = Storage.Split(';');
            if (storedData.Length == 3)
            {
                Boolean.TryParse(storedData[0], out hoverMode);
                Boolean.TryParse(storedData[1], out manual);
                Double.TryParse(storedData[2], out levitationHeight);
            }
        }

        public void Save()
        {
            Storage = string.Join(";", hoverMode, manual, levitationHeight);
        }

        public void SetHeight()
        {
            Double.TryParse(_commandLine.Argument(1), out levitationHeight);
        }

        public void ModHeight()
        {
            double temp;
            Double.TryParse(_commandLine.Argument(1), out temp);
            levitationHeight += temp;
        }

        public void ToggleHover()
        {
            hoverMode = !hoverMode;
        }

        public void ManualMode()
        {
            manual = !manual;
        }

        public void ResetHeight()
        {
            levitationHeight = defaultHeight;
        }

        public void Main(string argument, UpdateType updateSource)
        {
            if (_commandLine.TryParse(argument))
            {
                Action commandAction;
                string command = _commandLine.Argument(0);
                if (command == null)
                {
                    Echo("No command specified");
                }
                else if (_commands.TryGetValue(command, out commandAction))
                {
                    commandAction();
                }
                else
                {
                    Echo($"Unknown command {command}");
                }
            }
            if (hoverMode)
            {
                if (levitationHeight < defaultHeight)
                {
                    levitationHeight = defaultHeight;
                }
                if (manual)
                {
                    levitationHeight += controller.MoveIndicator.Y;
                }

                //update constants
                controller.TryGetPlanetElevation(MyPlanetElevation.Surface, out shipElevation);
                shipMass = controller.CalculateShipMass().PhysicalMass;
                gravity = controller.GetNaturalGravity().Length();
                shipDisplacement = (levitationHeight - shipElevation);
                offGroundVelocity = (shipElevation - previousElevation);
                offGroundAcceleration = (offGroundVelocity - previousVelocity);
                offGroundJerk = (offGroundAcceleration - previousAcceleration);
                previousAcceleration = offGroundAcceleration;
                previousVelocity = offGroundVelocity;
                previousElevation = shipElevation;

                T2W = thrusters.Sum(thruster => thruster.MaxEffectiveThrust) / (gravity * shipMass);

                J_PID = (float)_pid.Control((offGroundJerk));

                // calculate thrust
                // Total demanded thrust / sum of MaxEffectiveThrust across all upwards thrusters
                thrustPercentage = (float)(shipMass * gravity + offGroundAcceleration * shipMass + (shipDisplacement * spring - offGroundVelocity * dashpot + J_PID) * T2W * shipMass) / thrusters.Sum(thruster => thruster.MaxEffectiveThrust);
                // {save this bad boy for alternate flight modes and debugging} totalRequiredThrust = (float)((shipMass*gravity)/thrusters.Count/thrustEfficiency);
                thrusters.ToList().ForEach(thruster => thruster.ThrustOverridePercentage = thrustPercentage > minimumThrust ? thrustPercentage : minimumThrust);
                // thruster.ThrustOverridePercentage = thruster.ThrustOverridePercentage > minimumThrust ? thruster.ThrustOverridePercentage : minimumThrust;
                Echo("Hover Control Enabled");
            }
            else
            {
                foreach (IMyThrust thruster in thrusters)
                {
                    thruster.ThrustOverride = 0;
                }
                Echo("Hover Control Disabled");
            }
            //display information
            Echo((manual & hoverMode) ? "Manual Height Enabled" : "Manual Height Disabled");
            Echo("T/W Ratio: " + T2W.ToString());
            Echo("Lev Height: " + levitationHeight.ToString());
            Echo("True Height: " + shipElevation.ToString());
        }

        public class PID
        {
            readonly double _kP = 0;
            readonly double _kI = 0;
            readonly double _kD = 0;

            double _timeStep = 0;
            double _inverseTimeStep = 0;
            double _errorSum = 0;
            double _lastError = 0;
            bool _firstRun = true;

            public double Value { get; private set; }

            public PID(double kP, double kI, double kD, double timeStep)
            {
                _kP = kP;
                _kI = kI;
                _kD = kD;
                _timeStep = timeStep;
                _inverseTimeStep = 1 / _timeStep;
            }

            protected virtual double GetIntegral(double currentError, double errorSum, double timeStep)
            {
                return errorSum + currentError * timeStep;
            }

            public double Control(double error)
            {
                //Compute derivative term
                var errorDerivative = (error - _lastError) * _inverseTimeStep;

                if (_firstRun)
                {
                    errorDerivative = 0;
                    _firstRun = false;
                }

                //Get error sum
                _errorSum = GetIntegral(error, _errorSum, _timeStep);

                //Store this error as last error
                _lastError = error;

                //Construct output
                this.Value = _kP * error + _kI * _errorSum + _kD * errorDerivative;
                return this.Value;
            }

            public double Control(double error, double timeStep)
            {
                if (timeStep != _timeStep)
                {
                    _timeStep = timeStep;
                    _inverseTimeStep = 1 / _timeStep;
                }
                return Control(error);
            }

            public void Reset()
            {
                _errorSum = 0;
                _lastError = 0;
                _firstRun = true;
            }
        }
    }
}
