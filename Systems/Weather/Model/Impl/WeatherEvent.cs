﻿using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Vintagestory.GameContent
{
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class WeatherEventState
    {
        public int Index;
        public float BaseStrength;
        public double ActiveUntilTotalHours;

        public float NearLightningRate;
        public float DistantLightningRate;
        public float LightningMinTemp;
        public EnumPrecipitationType PrecType = EnumPrecipitationType.Auto;

        public float ParticleSize;
    }

    public class WeatherEvent
    {
        public WeatherEventConfig config;

        protected SimplexNoise strengthNoiseGen;
        ICoreAPI api;
        LCGRandom rand;

        public WeatherEventState State = new WeatherEventState();

        public float Strength;
        internal float hereChance;

        public bool ShouldStop(float rainfall, float temperature)
        {
            return config.getWeight(rainfall, temperature) <= 0;
        }

        public WeatherEvent(ICoreAPI api, WeatherEventConfig config, int index, LCGRandom rand, int seed)
        {
            this.rand = rand;
            this.config = config;
            this.api = api;
            this.State.Index = index;

            if (config.StrengthNoise != null)
            {
                strengthNoiseGen = new SimplexNoise(config.StrengthNoise.Amplitudes, config.StrengthNoise.Frequencies, seed + index);
            }
        }


        public virtual void OnBeginUse()
        {
            State.BaseStrength = Strength = config.Strength.nextFloat(1, rand);
            State.ActiveUntilTotalHours = api.World.Calendar.TotalHours + config.DurationHours.nextFloat(1, rand);

            State.PrecType = config.PrecType;
            State.NearLightningRate = config.Lightning?.NearRate / 100f ?? 0;
            State.DistantLightningRate = config.Lightning?.DistantRate / 100f ?? 0;
            State.LightningMinTemp = config.Lightning?.MinTemperature ?? 0;
        }

        public virtual void Update(float dt)
        {
            if (strengthNoiseGen != null)
            {
                double timeAxis = api.World.Calendar.TotalDays / 10.0;
                Strength = State.BaseStrength + (float)GameMath.Clamp(strengthNoiseGen.Noise(0, timeAxis), 0, 1);
            }
        }

        public virtual string GetWindName()
        {
            return config.Name;
        }

        internal void updateHereChance(float rainfall, float temperature)
        {
            hereChance = config.getWeight(rainfall, temperature);
        }
    }
}
