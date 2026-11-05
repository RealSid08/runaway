using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Runaway.Sim;

namespace Runaway.Advisor
{
    public sealed class PlanAction
    {
        [JsonProperty("type")] public string Type;           // cool | stage | interior_attack | shelter | evacuate
        [JsonProperty("unit")] public string Unit;
        [JsonProperty("container")] public string Container;
        [JsonProperty("staging")] public string Staging;
        [JsonProperty("receptor")] public string Receptor;
        [JsonProperty("reason")] public string Reason;

        public Order ToOrder()
        {
            var o = new Order { Unit = Unit, Container = Container, Staging = Staging, Receptor = Receptor, Source = "advisor" };
            switch ((Type ?? "").ToLowerInvariant())
            {
                case "cool": o.Type = OrderType.Cool; break;
                case "stage": o.Type = OrderType.Stage; break;
                case "interior_attack": o.Type = OrderType.InteriorAttack; break;
                case "shelter": o.Type = OrderType.Shelter; break;
                case "evacuate": o.Type = OrderType.Evacuate; break;
                default: return null;
            }
            return o;
        }

        public string Describe(IncidentSim sim)
        {
            switch ((Type ?? "").ToLowerInvariant())
            {
                case "cool": return $"{Unit} cool {Container}";
                case "stage": return $"{Unit} to {Site.StagingName(Staging)}";
                case "interior_attack": return $"{Unit} interior attack on {Container}";
                case "shelter": return $"Shelter in place: {sim?.ReceptorById(Receptor)?.Name ?? Receptor}";
                case "evacuate": return $"Evacuate {sim?.ReceptorById(Receptor)?.Name ?? Receptor}";
            }
            return Type;
        }
    }

    public sealed class EffectMetric
    {
        [JsonProperty("label")] public string Label;
        [JsonProperty("without")] public string Without;
        [JsonProperty("with")] public string With;
    }

    public sealed class Recommendation
    {
        [JsonProperty("headline")] public string Headline;
        [JsonProperty("actions")] public List<PlanAction> Actions = new List<PlanAction>();
        [JsonProperty("rationale")] public string Rationale;
        [JsonProperty("evidence")] public List<string> Evidence = new List<string>();
        [JsonProperty("expected_effect")] public List<EffectMetric> ExpectedEffect = new List<EffectMetric>();
        [JsonProperty("confidence")] public string Confidence;         // low | medium | high
        [JsonProperty("confidence_reason")] public string ConfidenceReason;
        [JsonProperty("would_change_my_mind")] public string WouldChangeMind;
        [JsonProperty("sensor_concerns")] public List<string> SensorConcerns = new List<string>();
        [JsonProperty("lessons_cited")] public List<string> LessonsCited = new List<string>();

        public List<Order> ToOrders() => Actions.Select(a => a.ToOrder()).Where(o => o != null).ToList();

        public const string Schema = @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""required"": [""headline"", ""actions"", ""rationale"", ""evidence"", ""expected_effect"", ""confidence"", ""confidence_reason"", ""would_change_my_mind"", ""sensor_concerns"", ""lessons_cited""],
  ""properties"": {
    ""headline"": { ""type"": ""string"", ""description"": ""Imperative summary of the plan, at most 12 words."" },
    ""actions"": {
      ""type"": ""array"", ""maxItems"": 5,
      ""items"": {
        ""type"": ""object"", ""additionalProperties"": false,
        ""required"": [""type"", ""unit"", ""container"", ""staging"", ""receptor"", ""reason""],
        ""properties"": {
          ""type"": { ""type"": ""string"", ""enum"": [""cool"", ""stage"", ""interior_attack"", ""shelter"", ""evacuate""] },
          ""unit"": { ""type"": [""string"", ""null""] },
          ""container"": { ""type"": [""string"", ""null""] },
          ""staging"": { ""type"": [""string"", ""null""], ""enum"": [""north"", ""west"", ""south"", ""east"", null] },
          ""receptor"": { ""type"": [""string"", ""null""], ""enum"": [""homes"", ""school"", ""farm"", null] },
          ""reason"": { ""type"": ""string"", ""description"": ""Why this action, at most 14 words."" }
        }
      }
    },
    ""rationale"": { ""type"": ""string"", ""description"": ""Two or three plain sentences a fire officer can read in ten seconds."" },
    ""evidence"": { ""type"": ""array"", ""maxItems"": 4, ""items"": { ""type"": ""string"" }, ""description"": ""Specific readings with their source, e.g. 'A1 thermal camera: A3 skin 96 C'."" },
    ""expected_effect"": {
      ""type"": ""array"", ""maxItems"": 3,
      ""items"": {
        ""type"": ""object"", ""additionalProperties"": false,
        ""required"": [""label"", ""without"", ""with""],
        ""properties"": { ""label"": { ""type"": ""string"" }, ""without"": { ""type"": ""string"" }, ""with"": { ""type"": ""string"" } }
      }
    },
    ""confidence"": { ""type"": ""string"", ""enum"": [""low"", ""medium"", ""high""] },
    ""confidence_reason"": { ""type"": ""string"" },
    ""would_change_my_mind"": { ""type"": ""string"" },
    ""sensor_concerns"": { ""type"": ""array"", ""maxItems"": 3, ""items"": { ""type"": ""string"" } },
    ""lessons_cited"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  }
}";
    }

    public sealed class Lesson
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("title")] public string Title;
        [JsonProperty("lesson")] public string Text;
        [JsonProperty("when")] public string When;        // the situation it applies to
        [JsonProperty("source_run")] public string SourceRun;
        [JsonProperty("cited")] public int Cited;
    }

    public sealed class LessonDraft
    {
        [JsonProperty("lessons")] public List<Lesson> Lessons = new List<Lesson>();

        public const string Schema = @"{
  ""type"": ""object"", ""additionalProperties"": false, ""required"": [""lessons""],
  ""properties"": {
    ""lessons"": {
      ""type"": ""array"", ""maxItems"": 3,
      ""items"": {
        ""type"": ""object"", ""additionalProperties"": false, ""required"": [""title"", ""lesson"", ""when""],
        ""properties"": {
          ""title"": { ""type"": ""string"", ""description"": ""At most 8 words."" },
          ""lesson"": { ""type"": ""string"", ""description"": ""One or two sentences: what to do differently and why."" },
          ""when"": { ""type"": ""string"", ""description"": ""The observable situation in which this lesson applies."" }
        }
      }
    }
  }
}";
    }
}
