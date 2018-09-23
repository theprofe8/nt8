// 
// Copyright (C) 2018, NinjaTrader LLC <www.ninjatrader.com>.
// NinjaTrader reserves the right to modify or overwrite this NinjaScript component with each release.
//
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using System.Xml.Serialization;

using NinjaTrader.Core.FloatingPoint;
#endregion

namespace NinjaTrader.NinjaScript.Optimizers
{
	public class UniversalOptimizer : Optimizer
	{
		internal Dictionary<Type, Tuple<double, double>> IndicatorTypes
		{ get; set; }

		private Cbi.SystemPerformance[] GetUniqueResults()
		{
			// filter duplicate results and keep the ones with the least # of nodes
			// note: filtering is not 100% inline with the logic to mutate/crossover/... the next generation
			// however it does not matter at the long run, as long as on starting the new generation there are still .GenerationSize individuals in the population
			List<Cbi.SystemPerformance> uniqueResults = new List<Cbi.SystemPerformance>();
			foreach (Cbi.SystemPerformance result in Results)
			{
				List<Cbi.SystemPerformance> results = Results.Where(r => result.ParameterValues != null
															&& result.PerformanceValue.ApproxCompare(r.PerformanceValue) == 0
															&& result.AllTrades.TradesPerformance.Percent.CumProfit.ApproxCompare(r.AllTrades.TradesPerformance.Percent.CumProfit) == 0
															&& result.AllTrades.LosingTrades.TradesCount == r.AllTrades.LosingTrades.TradesCount
															&& result.AllTrades.WinningTrades.TradesCount == r.AllTrades.WinningTrades.TradesCount).ToList();

				if (results.Count == 0)
					continue;

				results.Sort((a, b) => (a.ParameterValues[0] as Universal.Universal).NumNodes.CompareTo((b.ParameterValues[0] as Universal.Universal).NumNodes));

				if (!uniqueResults.Contains(results[0]))
					uniqueResults.Add(results[0]);
			}

			return uniqueResults.ToArray();
		}

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Generations							= 10;
				GenerationSize						= 40;
				Name								= Custom.Resource.NinjaScriptOptimizerUniversal;
				OptimizeEntries						= true;
				OptimizeExits						= true;
				OptimizeSessionClose				= true;
				UseCandleStickPatternForEntries		= true;
				UseCandleStickPatternForExits		= true;
				UseIndicatorsForEntries				= true;
				UseIndicatorsForExits				= true;
				UseParabolicStopForExits			= true;
				UseStopTargetsForExits				= true;
			}
			else if (State == State.Configure)
				NumberOfIterations					= Generations * GenerationSize;

				// Subjective selection of indicators which 'make most sense'. The full set of indicators would be used if this selection would not be set
				// The optional tuple defines the range of random comparison values for oscillators (where such range could be defined)
				// Feel free to modify to your liking...
				IndicatorTypes						= new Dictionary<Type, Tuple<double, double>>()
														{
															{ typeof(Indicators.ADX),				new Tuple<double, double>(0, 100) },
															{ typeof(Indicators.ATR),				null },
															{ typeof(Indicators.Bollinger),			null },
															{ typeof(Indicators.CCI),				new Tuple<double, double>(-400, 400) },
															{ typeof(Indicators.ChaikinOscillator),	new Tuple<double, double>(-1000, 1000) },
															{ typeof(Indicators.DM),				new Tuple<double, double>(-100, 100) },
															{ typeof(Indicators.DMI),				new Tuple<double, double>(-100, 100) },
															{ typeof(Indicators.EMA),				null },
															{ typeof(Indicators.FisherTransform),	new Tuple<double, double>(-10, 10) },
															{ typeof(Indicators.KeltnerChannel),	null },
															{ typeof(Indicators.MACD),				null },
															{ typeof(Indicators.Momentum),			null },
															{ typeof(Indicators.Range),				null },
															{ typeof(Indicators.ROC),				new Tuple<double, double>(-1, 1) },
															{ typeof(Indicators.RSI),				new Tuple<double, double>(0, 100) },
															{ typeof(Indicators.StdDev),			null },
															{ typeof(Indicators.SMA),				null },
															{ typeof(Indicators.Stochastics),		new Tuple<double, double>(0, 100) },
															{ typeof(Indicators.StochasticsFast),	new Tuple<double, double>(0, 100) },
															{ typeof(Indicators.TMA),				null },
															{ typeof(Indicators.TRIX),				null },
															{ typeof(Indicators.VROC),				null },
															{ typeof(Indicators.VOL),				null },
															{ typeof(Indicators.WMA),				null },
														};
		}

		protected override void OnOptimize()
		{		
			if (!UseCandleStickPatternForEntries && !UseIndicatorsForEntries)
				throw new ArgumentException(string.Format(Custom.Resource.NinjaScriptUniversalOptimizerEntriesNotDefined,
					Resource.NinjaScriptUniversalOptimizerUseCandleStickPattern, Resource.NinjaScriptUniversalOptimizerUseIndicators));

			if (!UseCandleStickPatternForExits && !UseIndicatorsForExits && !UseParabolicStopForExits && !UseStopTargetsForExits)
				throw new ArgumentException(string.Format(Custom.Resource.NinjaScriptUniversalOptimizerExitsNotDefined,
					Resource.NinjaScriptUniversalOptimizerUseCandleStickPattern, Resource.NinjaScriptUniversalOptimizerUseIndicators, Resource.NinjaScriptUniversalOptimizerUseStopTargets));

			if (!OptimizeEntries && !OptimizeExits)
				throw new ArgumentException(Custom.Resource.NinjaScriptUniversalOptimizerEntriesOrExits);

			// don't set these in Universal.OnStateChange since this would prevent SA from showing trade/executions details
			Strategies[0].IncludeTradeHistoryInBacktest				= false;										// no trade history needed
			Strategies[0].SupportsOptimizationGraph					= false;										// not needed

			Universal.Universal[]	population						= null;
			Random					random							= new Random();									// use only 1 instance to maximize randomness
			bool					resetAll						= true;
			int						trueStabilityIndividuals		= (int) (GenerationSize * 0.25);
			int						trueMutationIndividuals			= Math.Max(0, Math.Min(GenerationSize - trueStabilityIndividuals, (int) (GenerationSize * 0.25)));
			int						trueCrossOverIndividuals		= Math.Max(0, Math.Min(GenerationSize - trueStabilityIndividuals - trueMutationIndividuals, (int) (GenerationSize * 0.25)));
			
			for (int generation = 0; generation < Generations; generation++)
			{
				List<string> uniqueIndividuals = new List<string>();
				if (generation == 0)
				{
					population = new Universal.Universal[GenerationSize];
					for (int k = 0; k < population.Length; k++)
						population[k] = new Universal.Universal { UniversalOptimizer = this }.NewRandom(random);
				}
				else
				{
					Cbi.SystemPerformance[] uniqueResults = GetUniqueResults();

					// just re-run all individuals in case there had been 'duplicate' results
					resetAll = (uniqueResults.Length < Results.Length);

					// keep all individuals which are 'stable'
					List<Universal.Universal> newPopulation	= new List<Universal.Universal>();
					for (int k = 0; k < Math.Min(uniqueResults.Length, trueStabilityIndividuals); k++)
						newPopulation.Add(uniqueResults[k].ParameterValues[0] as Universal.Universal);

					// append all remaining individuals = those which are not 'stable' (and will be 'changed' by mutation or crossover or new, random generation)
					newPopulation.AddRange(population.Where(p1 => newPopulation.FirstOrDefault(p2 => p2.Id == p1.Id) == null));

					// fill up the population in case results had been removed
					while (newPopulation.Count < GenerationSize)
					{
						Universal.Universal individual = null;
						while (uniqueIndividuals.Contains((individual = new Universal.Universal { UniversalOptimizer = this }.NewRandom(random)).ToString())) {}
						newPopulation.Add(individual);
					}

					population = newPopulation.ToArray();

					// keep the stable individuals. No point to re-run a backtest 
					Reset(resetAll ? 0 : Math.Min(KeepBestResults, trueStabilityIndividuals));

					for (int k = 0; k < population.Length; k++)
					{
						Universal.Universal individual = null;
						if (k < trueStabilityIndividuals)
						{ 
							if (resetAll)
								population[k] = population[k].Clone() as Universal.Universal;

							individual = population[k];
						}
						else if (k < trueStabilityIndividuals + trueMutationIndividuals)
							while (uniqueIndividuals.Contains((individual = population[k].NewMutation(random)).ToString())) {}
						else if (k < trueStabilityIndividuals + trueMutationIndividuals + trueCrossOverIndividuals)
						{
							for (int m = 0; ; m++)
							{
								Universal.Universal fitter = population[random.Next(trueStabilityIndividuals)];
								
								// find a stable individual with similar long/short pattern
								if ((population[k].EnterLongCondition == null && fitter.EnterLongCondition == null
										|| population[k].EnterLongCondition != null && fitter.EnterLongCondition != null)
									&& (population[k].EnterShortCondition == null && fitter.EnterShortCondition == null
										|| population[k].EnterShortCondition != null && fitter.EnterShortCondition != null))
								{
									for (int j = 0; ; j++)				// crossover and try to create a new individual
									{
										if (!uniqueIndividuals.Contains((individual = population[k].NewCrossOver(fitter, random)).ToString()))
											break;
										else if (j >= GenerationSize)	// no success -> inject a new random indivdual
										{
											while (uniqueIndividuals.Contains((individual = new Universal.Universal { UniversalOptimizer = this }.NewRandom(random)).ToString())) {}
											break;
										}
									}
									break;
								}
								else if (m >= trueStabilityIndividuals) // no success -> inject a new random indivdual
								{
									while (uniqueIndividuals.Contains((individual = new Universal.Universal { UniversalOptimizer = this }.NewRandom(random)).ToString())) {}
									break;
								}
							}
						}
						else
							while (uniqueIndividuals.Contains((individual = new Universal.Universal { UniversalOptimizer = this }.NewRandom(random)).ToString())) {}

						uniqueIndividuals.Add(individual.ToString());
						population[k] = individual;
					}
				}

				for (int k = 0; k < population.Length; k++)
				{
					if (!resetAll && generation > 0 && k < Math.Min(KeepBestResults, trueStabilityIndividuals))
					{
						if (Progress != null)
							Progress.PerformStep();
					}
					else
					{
						Strategies[0].Universal = population[k];
						RunIteration();
					}
				}

				WaitForIterationsCompleted();
			}

			Results = GetUniqueResults();
		}

#region UI Properties
		[Display(ResourceType = typeof(Custom.Resource), Name = "NinjaScriptGeneticOptimizerGenerations")]
		[Range(1, Int32.MaxValue)]
		public int Generations
		{ get; set; }

		[Display(ResourceType = typeof(Custom.Resource), Name = "NinjaScriptGeneticOptimizerGenerationSize")]
		[Range(1, Int32.MaxValue)]
		public int GenerationSize
		{ get; set; }

		[Display(ResourceType = typeof(Custom.Resource), Name = "NinjaScriptUniversalOptimizeEntries")]
		public bool OptimizeEntries
		{ get; set; }

		[Display(ResourceType = typeof(Custom.Resource), Name = "NinjaScriptUniversalOptimizeExits")]
		public bool OptimizeExits
		{ get; set; }

		[Display(ResourceType = typeof(Resource), GroupName = "NinjaScriptUniversalOptimizerExits", Name = "NinjaScriptUniversalOptimizerSessionClose", Order = 5)]
		public bool OptimizeSessionClose
		{ get; set; }

		[Display(ResourceType = typeof(Resource), GroupName = "NinjaScriptUniversalOptimizerEntries", Name = "NinjaScriptUniversalOptimizerUseCandleStickPattern")]
		public bool UseCandleStickPatternForEntries
		{ get; set; }

		[Display(ResourceType = typeof(Resource), GroupName = "NinjaScriptUniversalOptimizerExits", Name = "NinjaScriptUniversalOptimizerUseCandleStickPattern", Order = 1)]
		public bool UseCandleStickPatternForExits
		{ get; set; }

		[Display(ResourceType = typeof(Resource), GroupName = "NinjaScriptUniversalOptimizerEntries", Name = "NinjaScriptUniversalOptimizerUseIndicators")]
		public bool UseIndicatorsForEntries
		{ get; set; }

		[Display(ResourceType = typeof(Resource), GroupName = "NinjaScriptUniversalOptimizerExits", Name = "NinjaScriptUniversalOptimizerUseIndicators", Order = 2)]
		public bool UseIndicatorsForExits
		{ get; set; }

		[Display(ResourceType = typeof(Resource), GroupName = "NinjaScriptUniversalOptimizerExits", Name = "NinjaScriptUniversalOptimizerUseParabolicStops", Order = 3)]
		public bool UseParabolicStopForExits
		{ get; set; }

		[Display(ResourceType = typeof(Resource), GroupName = "NinjaScriptUniversalOptimizerExits", Name = "NinjaScriptUniversalOptimizerUseStopTargets", Order = 4)]
		public bool UseStopTargetsForExits
		{ get; set; }
#endregion
	}
}

namespace NinjaTrader.NinjaScript.Universal
{
	internal static class Extentions
	{
		internal static void Indent(this StringBuilder stringBuilder, int tabLevels)
		{
			for (int i = 0; i < tabLevels; i++)
				stringBuilder.Append('\t');
		}
	}

	internal enum LogicalOperator
	{
		And,
		Not,
		Or,
	}

	internal interface IExpression : ICloneable
	{
		bool						Evaluate(Universal universal, StrategyBase strategy);
		List<IExpression>			GetExpressions();
		void						Initialize(StrategyBase strategy);
		IExpression					NewMutation(Universal universal, Random random, IExpression toMutate);
		void						Print(StringBuilder stringBuilder, int indentationLevel);
		XElement					ToXml();
	}

	internal class CandleStickPatternExpression : IExpression
	{
		private Indicators.CandleStickPatternLogic candleStickPatternLogic;

		public CandleStickPatternExpression()
		{
			Pattern = ChartPattern.MorningStar;
		}

		public object Clone()
		{
			return new CandleStickPatternExpression { Pattern = Pattern };
		}

		public bool Evaluate(Universal u, StrategyBase s)
		{
			if (candleStickPatternLogic == null)
				candleStickPatternLogic = new Indicators.CandleStickPatternLogic(s, u.TrendStrength);

			return candleStickPatternLogic.Evaluate(Pattern);
		}

		public static IExpression FromXml(XElement element)
		{
			return new CandleStickPatternExpression { Pattern = (ChartPattern) Enum.Parse(typeof(ChartPattern), element.Element("Pattern").Value) };
		}

		public List<IExpression> GetExpressions()
		{
			return new List<IExpression>(new IExpression[] { this });
		}

		public void Initialize(StrategyBase strategy)
		{
			// nothing to do here
		}

		public IExpression NewMutation(Universal universal, Random random, IExpression toMutate)
		{
			return new CandleStickPatternExpression { Pattern = (ChartPattern) random.Next(Universal.NumChartPattern) };
		}

		public ChartPattern Pattern
		{ get; set; }

		public void Print(StringBuilder s, int indentationLevel)
		{
			s.Append("candleStickPatternLogic.Evaluate(ChartPattern." + Pattern.ToString() + ")");
		}

		public XElement ToXml()
		{
			XElement ret = new XElement(GetType().Name);

			ret.Add(new XElement("Pattern", Pattern.ToString()));

			return ret;
		}
	}

	internal class IndicatorExpression : IExpression
	{
		private		double		maxCompare	= double.NaN;
		private		double		minCompare	= double.NaN;

		public object Clone()
		{
			IndicatorBase left	= (IndicatorBase) Left.Clone();
			IndicatorBase right = (IndicatorBase) Right.Clone();

			try 
			{
				left.SetState(State.Configure);
			}
			catch (Exception exp)
			{
				Cbi.Log.Process(typeof(Resource), "CbiUnableToCreateInstance2", new object[] { Left.Name, exp.InnerException != null ? exp.InnerException.ToString() : exp.ToString() }, Cbi.LogLevel.Error, Cbi.LogCategories.Default);
				left.SetState(State.Finalized);
			}			
			
			try 
			{
				right.SetState(State.Configure);
			}
			catch (Exception exp)
			{
				Cbi.Log.Process(typeof(Resource), "CbiUnableToCreateInstance2", new object[] { Right.Name, exp.InnerException != null ? exp.InnerException.ToString() : exp.ToString() }, Cbi.LogLevel.Error, Cbi.LogCategories.Default);
				right.SetState(State.Finalized);
			}			

			left.SelectedValueSeries	= Left.SelectedValueSeries;			// had to take care here since not copied by cloning
			right.SelectedValueSeries	= Right.SelectedValueSeries;

			return new IndicatorExpression { ComparePercent = ComparePercent, Condition = Condition, Left = left, LeftBarsAgo = LeftBarsAgo, Right = right, RightBarsAgo = RightBarsAgo, UsePriceToCompare = UsePriceToCompare };
		}

		public double ComparePercent
		{ get; set; }

		private double CompareValue
		{ 
			get { return minCompare + (maxCompare - minCompare) * ComparePercent; }
		}

		public Condition Condition
		{ get; set; }

		public bool Evaluate(Universal universal, StrategyBase strategy)
		{
			switch (Condition)
			{
				case Condition.CrossAbove:
				{
					int		aboveIdx		= -1;
					int		lookBackPeriod	= 1;

					if (Left.IsOverlay)
					{
						ISeries<double>	series	= (UsePriceToCompare ? Left.Close : Right);
						int				maxBack	= Math.Min(series.Count - 1, Math.Min(lookBackPeriod, Left.Count - 1));
						for (int idx = 0; idx <= maxBack; idx++)
						{
							if (aboveIdx < 0 && Left[idx] > series[idx])
								aboveIdx = idx;
							else if (aboveIdx >= 0 && Left[idx] <= series[idx])
								return true;
						}
					}
					else
					{
						int	maxBack			= Math.Min(lookBackPeriod, Left.Count - 1);
						for (int idx = 0; idx <= maxBack; idx++)
						{
							if (aboveIdx < 0 && Left[idx] > CompareValue)
								aboveIdx = idx;
							else if (aboveIdx >= 0 && Left[idx] <= CompareValue)
								return true;
						}
					}

					return false;
				}
				case Condition.CrossBelow:
				{
					int		belowIdx		= -1;
					int		lookBackPeriod	= 1;

					if (Left.IsOverlay)
					{
						ISeries<double>	series	= (UsePriceToCompare ? Left.Close : Right);
						int				maxBack	= Math.Min(series.Count - 1, Math.Min(lookBackPeriod, Left.Count - 1));
						for (int idx = 0; idx <= maxBack; idx++)
						{
							if (belowIdx < 0 && Left[idx] < series[idx])
								belowIdx = idx;
							else if (belowIdx >= 0 && Left[idx] >= series[idx])
								return true;
						}
					}
					else
					{
						int	maxBack			= Math.Min(lookBackPeriod, Left.Count - 1);
						for (int idx = 0; idx <= maxBack; idx++)
						{
							if (belowIdx < 0 && Left[idx] < CompareValue)
								belowIdx = idx;
							else if (belowIdx >= 0 && Left[idx] >= CompareValue)
								return true;
						}
					}
					return false;
				}
				case Condition.Equals:			return Left[LeftBarsAgo].ApproxCompare(Left.IsOverlay ? (UsePriceToCompare ? Left.Close[RightBarsAgo] : Right[RightBarsAgo]) : CompareValue) == 0;
				case Condition.Greater:			return Left[LeftBarsAgo].ApproxCompare(Left.IsOverlay ? (UsePriceToCompare ? Left.Close[RightBarsAgo] : Right[RightBarsAgo]) : CompareValue) > 0;
				case Condition.GreaterEqual:	return Left[LeftBarsAgo].ApproxCompare(Left.IsOverlay ? (UsePriceToCompare ? Left.Close[RightBarsAgo] : Right[RightBarsAgo]) : CompareValue) >= 0;
				case Condition.Less:			return Left[LeftBarsAgo].ApproxCompare(Left.IsOverlay ? (UsePriceToCompare ? Left.Close[RightBarsAgo] : Right[RightBarsAgo]) : CompareValue) < 0;
				case Condition.LessEqual:		return Left[LeftBarsAgo].ApproxCompare(Left.IsOverlay ? (UsePriceToCompare ? Left.Close[RightBarsAgo] : Right[RightBarsAgo]) : CompareValue) <= 0;
				case Condition.NotEqual:		return Left[LeftBarsAgo].ApproxCompare(Left.IsOverlay ? (UsePriceToCompare ? Left.Close[RightBarsAgo] : Right[RightBarsAgo]) : CompareValue) != 0;
				default:						return false;
			}
		}

		public static IExpression FromXml(XElement element)
		{
			IndicatorExpression ret = new IndicatorExpression
										{
											ComparePercent		= double.Parse(element.Element("ComparePercent").Value, CultureInfo.InvariantCulture),
											Condition			= (Condition) Enum.Parse(typeof(Condition), element.Element("Condition").Value),
											LeftBarsAgo			= int.Parse(element.Element("LeftBarsAgo").Value),
											maxCompare			= double.Parse(element.Element("MaxCompare").Value, CultureInfo.InvariantCulture),
											minCompare			= double.Parse(element.Element("MinCompare").Value, CultureInfo.InvariantCulture),
											RightBarsAgo		= int.Parse(element.Element("RightBarsAgo").Value),
											UsePriceToCompare	= bool.Parse(element.Element("UsePriceToCompare").Value),
										};

			if (element.Element("LeftType") != null)
				ret.Left = new XmlSerializer(Core.Globals.AssemblyRegistry.GetType(element.Element("LeftType").Value)).Deserialize(element.Element("Left").FirstNode.CreateReader()) as IndicatorBase;

			if (element.Element("RightType") != null)
				ret.Right = new XmlSerializer(Core.Globals.AssemblyRegistry.GetType(element.Element("RightType").Value)).Deserialize(element.Element("Right").FirstNode.CreateReader()) as IndicatorBase;

			return ret;
		}
		
		public List<IExpression> GetExpressions()
		{
			return new List<IExpression>(new IExpression[] { this });
		}

		public void Initialize(StrategyBase strategy)
		{
			Left.Parent		= strategy;
			Right.Parent	= strategy;
			Left.SetInput(strategy.Input);
			Right.SetInput(strategy.Input);

			lock (strategy.NinjaScripts)
			{
				strategy.NinjaScripts.Add(Left);
				strategy.NinjaScripts.Add(Right);
			}

			try
			{
				Left.SetState(strategy.State);
			}
			catch (Exception exp)
			{
				Cbi.Log.Process(typeof(Resource), "CbiUnableToCreateInstance2", new object[] { Left.Name, exp.InnerException != null ? exp.InnerException.ToString() : exp.ToString() }, Cbi.LogLevel.Error, Cbi.LogCategories.Default);
				Left.SetState(State.Finalized);
				return;
			}

			try
			{
				Right.SetState(strategy.State);
			}
			catch (Exception exp)
			{
				Cbi.Log.Process(typeof(Resource), "CbiUnableToCreateInstance2", new object[] { Right.Name, exp.InnerException != null ? exp.InnerException.ToString() : exp.ToString() }, Cbi.LogLevel.Error, Cbi.LogCategories.Default);
				Right.SetState(State.Finalized);
				return;
			}

			if (!Left.IsOverlay && double.IsNaN(maxCompare))
			{
				Left.Update(Left.BarsArray[0].Count - 1, 0);

				maxCompare = double.MinValue;
				minCompare = double.MaxValue;
				for (int i = 0; i < Left.BarsArray[0].Count; i++)									// find min/max range, .CompareValue will be in between
				{
					maxCompare = Math.Max(maxCompare,	Left.Values[Left.SelectedValueSeries].GetValueAt(i));
					minCompare = Math.Min(minCompare,	Left.Values[Left.SelectedValueSeries].GetValueAt(i));
				}
			}
		}
		
		public IndicatorBase Left
		{ get; set; }

		public int LeftBarsAgo
		{ get; set; }

		public IExpression NewMutation(Universal universal, Random random, IExpression toMutate)
		{
			// < 2:		mutate .Condition
			// < 4:		mutate .Left
			// < 6:		mutate .Right/.UsePriceToCompare or .CompareValue
			// < 8:		mutate .Left's .SelectedValueSeries
			// < 10:	mutate .Right's .SelectedValueSeries
			// < 20:	mutate random .Left property
			// < 30:	mutate random .Right property
			// < 40:	mutate .LeftBarsAgo between 0 and 9
			// < 50:	mutate .RightBarsAgo between 0 and 9
			
			IndicatorExpression	ret = null;

			while (true)
			{
				int r = random.Next(50);

				ret = new IndicatorExpression
				{
					ComparePercent	= ComparePercent,
					Condition		= (toMutate == this && r >= 0 && r < 2 ? (Condition) random.Next(Universal.NumConditions) : Condition),
					Left			= (toMutate == this && r >= 2 && r < 4 ? universal.RandomIndicator(random) : (IndicatorBase) Left.Clone()),
					LeftBarsAgo		= LeftBarsAgo,
					Right			= (toMutate == this && r >= 4 && r < 6 ? universal.RandomIndicator(random) : (IndicatorBase) Right.Clone()),
					RightBarsAgo	= RightBarsAgo,
				};

				if (ret.Left.IsOverlay)
					while (!ret.Right.IsOverlay)
					{
						try { ret.Right.SetState(State.Finalized); } catch {}
						ret.Right = universal.RandomIndicator(random);
					}
				else
					while (ret.Right.IsOverlay)
					{
						try { ret.Right.SetState(State.Finalized); } catch {}
						ret.Right = universal.RandomIndicator(random);
					}

				// make sure .State >= .Configure
				try
				{
					ret.Left.SetState(State.Configure);
				}
				catch (Exception exp)
				{
					Cbi.Log.Process(typeof(Resource), "CbiUnableToCreateInstance2", new object[] { ret.Left.Name, exp.InnerException != null ? exp.InnerException.ToString() : exp.ToString() }, Cbi.LogLevel.Error, Cbi.LogCategories.Default);
					ret.Left.SetState(State.Finalized);
					continue;
				}

				try
				{
					ret.Right.SetState(State.Configure);
				}
				catch (Exception exp)
				{
					Cbi.Log.Process(typeof(Resource), "CbiUnableToCreateInstance2", new object[] { ret.Right.Name, exp.InnerException != null ? exp.InnerException.ToString() : exp.ToString() }, Cbi.LogLevel.Error, Cbi.LogCategories.Default);
					ret.Left.SetState(State.Finalized);
					ret.Right.SetState(State.Finalized);
					continue;
				}

				if (!(toMutate == this && r >= 2 && r < 4))
					ret.Left.SelectedValueSeries = Left.SelectedValueSeries;

				if (!(toMutate == this && r >= 4 && r < 6))
					ret.Right.SelectedValueSeries = Right.SelectedValueSeries;

				if (toMutate == this)
				{
					if (r >= 2 && r < 4)
					{
						ret.maxCompare = double.NaN;
						ret.minCompare = double.NaN;
					}
					else if (r >= 4 && r < 6)
					{
						if (!double.IsNaN(ret.maxCompare))
							ret.ComparePercent = Math.Min(1, Math.Max(0, ret.ComparePercent * (random.Next(2) == 0 ? 0.9 : 1.1)));

						ret.UsePriceToCompare = (random.Next(2) == 0);
					}
					else if (r >= 10 && r < 30)
					{
						IndicatorBase	indicator = (r < 20 ? ret.Left : ret.Right);
						double			value;

						List<PropertyInfo> properties = indicator.GetType().GetProperties().Where(p => Attribute.GetCustomAttribute(p, typeof(RangeAttribute), false) != null
																									&& Attribute.GetCustomAttribute(p, typeof(NinjaScriptPropertyAttribute), false) != null).ToList();
						if (properties.Count == 0 || indicator.State == State.Finalized)
							continue;

						PropertyInfo propertyInfo = properties[random.Next(properties.Count)];
						try
						{
							value = (double) Convert.ChangeType(propertyInfo.GetValue(indicator, null), typeof(double));
						}
						catch (Exception exp)
						{
							indicator.LogAndPrint(typeof(Resource), "DataGetPropertyValueException", new object[] { propertyInfo.Name, indicator.Name, NinjaScriptBase.GetExceptionMessage(exp) }, Cbi.LogLevel.Error); 
							indicator.SetState(State.Finalized);
							continue;
						}

						double			changeFactor	= (random.Next(2) == 0 ? 0.75 : 1.25);
						RangeAttribute	rangeAttribute	= Attribute.GetCustomAttribute(propertyInfo, typeof(RangeAttribute), false) as RangeAttribute;
						double			maximum			= (double) Convert.ChangeType(rangeAttribute.Maximum, typeof(double));
						double			minimum			= (double) Convert.ChangeType(rangeAttribute.Minimum, typeof(double));

						// make sure we're not looking back further than MaximumBarsLookBack.TwoHundredFiftySix
						// this works off the assumption that all 'period' properties are 'int' and all 'int' properties should not reasonably excceed the value range of 256 - N
						if (propertyInfo.PropertyType == typeof(int))
							maximum = 256 - 10;							// just have N = 10

						try
						{
							value = Math.Max(minimum, Math.Min(maximum, value * changeFactor));

							propertyInfo.SetValue(indicator, Convert.ChangeType(value, propertyInfo.PropertyType));
						}
						catch (Exception exp)
						{
							indicator.LogAndPrint(typeof(Resource), "DataGetPropertyValueException", new object[] { propertyInfo.Name, indicator.Name, NinjaScriptBase.GetExceptionMessage(exp) }, Cbi.LogLevel.Error); 
							indicator.SetState(State.Finalized);
							continue;
						}
					}
					else if (r >= 6 && r < 10)
					{
						(r < 8 ? ret.Left : ret.Right).SelectedValueSeries = random.Next((r < 8 ? ret.Left : ret.Right).Values.Length);
				
						ret.maxCompare = double.NaN;
						ret.minCompare = double.NaN;
					}
					else if (r >= 30 && r < 40)
						ret.LeftBarsAgo = random.Next(10);
					else if (r >= 40 && r < 50)
						ret.RightBarsAgo = random.Next(10);
				}

				return ret;
			}
		}

		public void Print(StringBuilder s, int indentationLevel)
		{
			switch (Condition)
			{
				case Condition.CrossAbove:
					s.Append("CrossAbove(");
					s.Append(Left.GetDisplayName(true, true, false));
					if (Left.SelectedValueSeries != 0)
						s.Append(".Values[" + Left.SelectedValueSeries + "]");
					s.Append(", ");
					s.Append(Left.IsOverlay ? (UsePriceToCompare ? "Close" : Right.GetDisplayName(true, true, false)) + (Right.SelectedValueSeries != 0 ? ".Values[" + Right.SelectedValueSeries + "]" : string.Empty) : CompareValue.ToString(CultureInfo.InvariantCulture));
					s.Append(", 1)");
					break;
				
				case Condition.CrossBelow:
					s.Append("CrossBelow(");
					s.Append(Left.GetDisplayName(true, true, false));
					if (Left.SelectedValueSeries != 0)
						s.Append(".Values[" + Left.SelectedValueSeries + "]");
					s.Append(", ");
					s.Append(Left.IsOverlay ? (UsePriceToCompare ? "Close" : Right.GetDisplayName(true, true, false)) + (Right.SelectedValueSeries != 0 ? ".Values[" + Right.SelectedValueSeries + "]" : string.Empty) : CompareValue.ToString(CultureInfo.InvariantCulture));
					s.Append(", 1)");
					break;

				case Condition.Equals:
					s.Append(Left.GetDisplayName(true, true, false));
					if (Left.SelectedValueSeries != 0)
						s.Append(".Values[" + Left.SelectedValueSeries + "]");
					s.Append("[" + LeftBarsAgo + "].ApproxCompare(");
					s.Append(Left.IsOverlay ? (UsePriceToCompare ? "Close" : Right.GetDisplayName(true, true, false)) + (Right.SelectedValueSeries != 0 ? ".Values[" + Right.SelectedValueSeries + "]" : string.Empty) + "[" + RightBarsAgo + "]" : CompareValue.ToString(CultureInfo.InvariantCulture));
					s.Append(") == 0");
					break;

				case Condition.Greater:
					s.Append(Left.GetDisplayName(true, true, false));
					if (Left.SelectedValueSeries != 0)
						s.Append(".Values[" + Left.SelectedValueSeries + "]");
					s.Append("[" + LeftBarsAgo + "].ApproxCompare(");
					s.Append(Left.IsOverlay ? (UsePriceToCompare ? "Close" : Right.GetDisplayName(true, true, false)) + (Right.SelectedValueSeries != 0 ? ".Values[" + Right.SelectedValueSeries + "]" : string.Empty) + ".Values[" + Right.SelectedValueSeries + "][" + RightBarsAgo + "]" : CompareValue.ToString(CultureInfo.InvariantCulture));
					s.Append(") > 0");
					break;

				case Condition.GreaterEqual:
					s.Append(Left.GetDisplayName(true, true, false));
					if (Left.SelectedValueSeries != 0)
						s.Append(".Values[" + Left.SelectedValueSeries + "]");
					s.Append("[" + LeftBarsAgo + "].ApproxCompare(");
					s.Append(Left.IsOverlay ? (UsePriceToCompare ? "Close" : Right.GetDisplayName(true, true, false)) + (Right.SelectedValueSeries != 0 ? ".Values[" + Right.SelectedValueSeries + "]" : string.Empty) + ".Values[" + Right.SelectedValueSeries + "][" + RightBarsAgo + "]" : CompareValue.ToString(CultureInfo.InvariantCulture));
					s.Append(") >= 0");
					break;

				case Condition.Less:
					s.Append(Left.GetDisplayName(true, true, false));
					if (Left.SelectedValueSeries != 0)
						s.Append(".Values[" + Left.SelectedValueSeries + "]");
					s.Append("[" + LeftBarsAgo + "].ApproxCompare(");
					s.Append(Left.IsOverlay ? (UsePriceToCompare ? "Close" : Right.GetDisplayName(true, true, false)) + (Right.SelectedValueSeries != 0 ? ".Values[" + Right.SelectedValueSeries + "]" : string.Empty) + ".Values[" + Right.SelectedValueSeries + "][" + RightBarsAgo + "]" : CompareValue.ToString(CultureInfo.InvariantCulture));
					s.Append(") < 0");
					break;

				case Condition.LessEqual:
					s.Append(Left.GetDisplayName(true, true, false));
					if (Left.SelectedValueSeries != 0)
						s.Append(".Values[" + Left.SelectedValueSeries + "]");
					s.Append("[" + LeftBarsAgo + "].ApproxCompare(");
					s.Append(Left.IsOverlay ? (UsePriceToCompare ? "Close" : Right.GetDisplayName(true, true, false)) + (Right.SelectedValueSeries != 0 ? ".Values[" + Right.SelectedValueSeries + "]" : string.Empty) + ".Values[" + Right.SelectedValueSeries + "][" + RightBarsAgo + "]" : CompareValue.ToString(CultureInfo.InvariantCulture));
					s.Append(") <= 0");
					break;

				case Condition.NotEqual:
					s.Append(Left.GetDisplayName(true, true, false));
					if (Left.SelectedValueSeries != 0)
						s.Append(".Values[" + Left.SelectedValueSeries + "]");
					s.Append("[" + LeftBarsAgo + "].ApproxCompare(");
					s.Append(Left.IsOverlay ?(UsePriceToCompare ? "Close" :  Right.GetDisplayName(true, true, false)) + (Right.SelectedValueSeries != 0 ? ".Values[" + Right.SelectedValueSeries + "]" : string.Empty) + ".Values[" + Right.SelectedValueSeries + "][" + RightBarsAgo + "]" : CompareValue.ToString(CultureInfo.InvariantCulture));
					s.Append(") != 0");
					break;
			}
		}

		public IndicatorBase Right
		{ get; set; }

		public int RightBarsAgo
		{ get; set; }

		public XElement ToXml()
		{
			XElement ret = new XElement(GetType().Name);

			if (Left != null)
				using (StringWriter stringWriter = new StringWriter(CultureInfo.InvariantCulture))
				{
					new XmlSerializer(Left.GetType()).Serialize(stringWriter, Left);

					ret.Add(new XElement("LeftType", Left.GetType().FullName));
					ret.Add(new XElement("Left", XElement.Parse(stringWriter.ToString())));
				}

			if (Right != null)
				using (StringWriter stringWriter = new StringWriter(CultureInfo.InvariantCulture))
				{
					new XmlSerializer(Right.GetType()).Serialize(stringWriter, Right);

					ret.Add(new XElement("RightType", Right.GetType().FullName));
					ret.Add(new XElement("Right", XElement.Parse(stringWriter.ToString())));
				}

			ret.Add(new XElement("ComparePercent", ComparePercent.ToString(CultureInfo.InvariantCulture)));
			ret.Add(new XElement("Condition", Condition.ToString()));
			ret.Add(new XElement("LeftBarsAgo", LeftBarsAgo.ToString()));
			ret.Add(new XElement("MaxCompare", maxCompare.ToString(CultureInfo.InvariantCulture)));
			ret.Add(new XElement("MinCompare", minCompare.ToString(CultureInfo.InvariantCulture)));
			ret.Add(new XElement("RightBarsAgo", RightBarsAgo.ToString()));
			ret.Add(new XElement("UsePriceToCompare", UsePriceToCompare.ToString()));

			return ret;
		}

		public bool UsePriceToCompare
		{ get; set; }
	}

	internal class LogicalExpression : IExpression
	{
		public object Clone()
		{
			return new LogicalExpression { Left = (IExpression) Left.Clone(), Operator = Operator, Right = (IExpression) Right.Clone() };
		}

		public bool Evaluate(Universal universal, StrategyBase strategy)
		{
			switch (Operator)
			{
				case LogicalOperator.And:	return Left.Evaluate(universal, strategy) && Right.Evaluate(universal, strategy);
				case LogicalOperator.Not:	return !Left.Evaluate(universal, strategy);
				case LogicalOperator.Or:	return Left.Evaluate(universal, strategy) || Right.Evaluate(universal, strategy);
				default:					return false;
			}
		}

		public static IExpression FromXml(XElement element)
		{
			return new LogicalExpression
						{
							Left		= element.Element("Left").Elements().First().Name == "IndicatorExpression" ? IndicatorExpression.FromXml(element.Element("Left").Elements().First()) 
											: (element.Element("Left").Elements().First().Name == "CandleStickPatternExpression" ? CandleStickPatternExpression.FromXml(element.Element("Left").Elements().First())  : FromXml(element.Element("Left").Elements().First())),
							Operator	= (LogicalOperator) Enum.Parse(typeof(LogicalOperator), element.Element("Operator").Value),
							Right		= element.Element("Right").Elements().First().Name == "IndicatorExpression" ? IndicatorExpression.FromXml(element.Element("Right").Elements().First()) 
											: (element.Element("Right").Elements().First().Name == "CandleStickPatternExpression" ? CandleStickPatternExpression.FromXml(element.Element("Right").Elements().First())  : FromXml(element.Element("Right").Elements().First())),
						};
		}

		public List<IExpression> GetExpressions()
		{
			List<IExpression> ret = new List<IExpression>();
			ret.Add(this);
			ret.AddRange(Left.GetExpressions());
			ret.AddRange(Right.GetExpressions());

			return ret;
		}

		public void Initialize(StrategyBase strategy)
		{
			Left.Initialize(strategy);
			Right.Initialize(strategy);
		}

		public IExpression Left
		{ get; set; }

		public IExpression NewMutation(Universal universal, Random random, IExpression toMutate)
		{
			int r = random.Next(10);

			return new LogicalExpression {
					Operator	= (toMutate == this && r < 6			? (LogicalOperator) random.Next(Universal.NumLogicalOperators) : Operator),
					Left		= (toMutate == this && r >= 6 && r < 8	? universal.RandomExpression(random) : Left.NewMutation(universal, random, toMutate)),
					Right		= (toMutate == this && r >= 8			? universal.RandomExpression(random) : Right.NewMutation(universal, random, toMutate)) };
		}

		public LogicalOperator Operator
		{ get; set; }

		public void Print(StringBuilder s, int indentationLevel)
		{
			switch (Operator)
			{
				case LogicalOperator.And:
					s.Append('('); Left.Print(s, indentationLevel + 1); s.Append(Environment.NewLine);
						s.Indent(indentationLevel); s.Append("&& "); Right.Print(s, indentationLevel + 1); s.Append(')'); 
					break;
				case LogicalOperator.Not:
					s.Append("!("); Left.Print(s, indentationLevel + 1); s.Append(')'); 
					break;
				case LogicalOperator.Or:
					s.Append('('); Left.Print(s, indentationLevel + 1); s.Append(Environment.NewLine);
						s.Indent(indentationLevel); s.Append("|| "); Right.Print(s, indentationLevel + 1); s.Append(')'); 
					break;
				default:
					break;
			}
		}

		public IExpression Right
		{ get; set; }

		public XElement ToXml()
		{
			XElement ret = new XElement(GetType().Name);

			ret.Add(new XElement("Left", Left.ToXml()));
			ret.Add(new XElement("Operator", Operator.ToString()));
			ret.Add(new XElement("Right", Right.ToXml()));

			return ret;
		}
	}

	public sealed class Universal : UniversalBase
	{
		private		int						isInitialized;
		private		static long				lastId					= -1;
		private		int						numNodes				= -1;
		private		double					stopTargetPercentStep	= 0.0025;		// intial stops are 0.25% 0.5% 0.75% 1% 1.25% 1.5% 1.75% or 2%, initial target is double that value
		private		static object			syncRoot				= new object();

		internal	static readonly int		NumChartPattern			= Enum.GetValues(typeof(ChartPattern)).Length;
		internal	static readonly int		NumConditions			= 7;
		internal	static readonly int		NumLogicalOperators		= Enum.GetValues(typeof(LogicalOperator)).Length;

		/// <summary>
		/// Create a clone.
		/// </summary>
		/// <returns></returns>
		public override object Clone()
		{
			Universal ret = new Universal
								{
									EnterLongCondition		= (EnterLongCondition	== null ? null : (IExpression) EnterLongCondition.Clone()),
									EnterShortCondition		= (EnterShortCondition	== null ? null : (IExpression) EnterShortCondition.Clone()),
									ExitLongCondition		= (ExitLongCondition	== null ? null : (IExpression) ExitLongCondition.Clone()),
									ExitShortCondition		= (ExitShortCondition	== null ? null : (IExpression) ExitShortCondition.Clone()),
									ExitOnSessionClose		= ExitOnSessionClose,
									Id						= Id,
									ParabolicStopPercent	= ParabolicStopPercent,
									ProfitTargetPercent		= ProfitTargetPercent,
									StopLossPercent			= StopLossPercent,
									TrailStopPercent		= TrailStopPercent,
									TrendStrength			= TrendStrength,
									UniversalOptimizer		= UniversalOptimizer,
								};

			return ret;
		}

		internal IExpression EnterLongCondition
		{ get; set; }

		internal IExpression EnterShortCondition
		{ get; set; }

		internal IExpression ExitLongCondition
		{ get; set; }

		internal IExpression ExitShortCondition
		{ get; set; }

		internal bool? ExitOnSessionClose
		{ get; set; }

		/// <summary>
		/// Populate an instance from XML
		/// </summary>
		/// <param name="element"></param>
		public override void FromXml(XElement element)
		{
			EnterLongCondition				= element.Element("EnterLongCondition")		!= null ? (element.Element("EnterLongCondition").Elements().First().Name	== "IndicatorExpression" ? IndicatorExpression.FromXml(element.Element("EnterLongCondition").Elements().First())	
												: (element.Element("EnterLongCondition").Elements().First().Name	== "CandleStickPatternExpression" ? CandleStickPatternExpression.FromXml(element.Element("EnterLongCondition").Elements().First()) : LogicalExpression.FromXml(element.Element("EnterLongCondition").Elements().First())))		: null;
			EnterShortCondition				= element.Element("EnterShortCondition")	!= null ? (element.Element("EnterShortCondition").Elements().First().Name	== "IndicatorExpression" ? IndicatorExpression.FromXml(element.Element("EnterShortCondition").Elements().First())
												: (element.Element("EnterShortCondition").Elements().First().Name	== "CandleStickPatternExpression" ? CandleStickPatternExpression.FromXml(element.Element("EnterShortCondition").Elements().First()) : LogicalExpression.FromXml(element.Element("EnterShortCondition").Elements().First())))	: null;
			ExitLongCondition				= element.Element("ExitLongCondition")		!= null ? (element.Element("ExitLongCondition").Elements().First().Name		== "IndicatorExpression" ? IndicatorExpression.FromXml(element.Element("ExitLongCondition").Elements().First())
												: (element.Element("ExitLongCondition").Elements().First().Name		== "CandleStickPatternExpression" ? CandleStickPatternExpression.FromXml(element.Element("ExitLongCondition").Elements().First()) : LogicalExpression.FromXml(element.Element("ExitLongCondition").Elements().First())))		: null;
			ExitShortCondition				= element.Element("ExitShortCondition")		!= null ? (element.Element("ExitShortCondition").Elements().First().Name	== "IndicatorExpression" ? IndicatorExpression.FromXml(element.Element("ExitShortCondition").Elements().First())
												: (element.Element("ExitShortCondition").Elements().First().Name	== "CandleStickPatternExpression" ? CandleStickPatternExpression.FromXml(element.Element("ExitShortCondition").Elements().First()) : LogicalExpression.FromXml(element.Element("ExitShortCondition").Elements().First())))		: null;

			if (element.Element("ExitOnSessionClose") != null)
				ExitOnSessionClose			= bool.Parse(element.Element("ExitOnSessionClose").Value);

			ParabolicStopPercent			= double.Parse(element.Element("ParabolicStopPercent").Value, CultureInfo.InvariantCulture);
			ProfitTargetPercent				= double.Parse(element.Element("ProfitTargetPercent").Value, CultureInfo.InvariantCulture);
			StopLossPercent					= double.Parse(element.Element("StopLossPercent").Value, CultureInfo.InvariantCulture);
			TrailStopPercent				= double.Parse(element.Element("TrailStopPercent").Value, CultureInfo.InvariantCulture);
			TrendStrength					= int.Parse(element.Element("TrendStrength").Value, CultureInfo.InvariantCulture);
		}

		public long Id
		{ get; private set; }

		public bool IsConsistent
		{
			get
			{ 
				return (EnterLongCondition != null || EnterShortCondition != null)
							&& (ExitLongCondition == null || EnterLongCondition != null)
							&& (ExitShortCondition == null || EnterShortCondition != null)
							&& ((double.IsNaN(StopLossPercent) && double.IsNaN(TrailStopPercent)) || !double.IsNaN(ProfitTargetPercent))
							&& ((double.IsNaN(ParabolicStopPercent) && ExitShortCondition == null) || double.IsNaN(ProfitTargetPercent))
							&& (ExitLongCondition != null || ExitShortCondition != null || !double.IsNaN(ParabolicStopPercent) || !double.IsNaN(StopLossPercent) || !double.IsNaN(TrailStopPercent))
							&& TrendStrength > 0;
			}
		}

		internal Universal NewCrossOver(Universal fitter, Random random)
		{
			// 0: entry
			// 1: exit
			// 2: trend strength
			int			r	= random.Next(3);
			Universal	ret = new Universal
							{
								EnterLongCondition				= (r == 0 ? (fitter.EnterLongCondition	!= null ? fitter.EnterLongCondition.Clone() as IExpression	: null)	: (EnterLongCondition	!= null ? EnterLongCondition.Clone() as IExpression		: null)),
								EnterShortCondition				= (r == 0 ? (fitter.EnterShortCondition	!= null ? fitter.EnterShortCondition.Clone() as IExpression : null)	: (EnterShortCondition	!= null ? EnterShortCondition.Clone() as IExpression	: null)),
								ExitLongCondition				= (r == 1 ? (fitter.ExitLongCondition	!= null ? fitter.ExitLongCondition.Clone() as IExpression	: null)	: (ExitLongCondition	!= null ? ExitLongCondition.Clone() as IExpression		: null)),
								ExitShortCondition				= (r == 1 ? (fitter.ExitShortCondition	!= null ? fitter.ExitShortCondition.Clone() as IExpression	: null)	: (ExitShortCondition	!= null ? ExitShortCondition.Clone() as IExpression		: null)),
								ExitOnSessionClose				= (r == 1 ? fitter.ExitOnSessionClose	: ExitOnSessionClose),
								ParabolicStopPercent			= (r == 1 ? fitter.ParabolicStopPercent	: ParabolicStopPercent),
								ProfitTargetPercent				= (r == 1 ? fitter.ProfitTargetPercent	: ProfitTargetPercent),
								StopLossPercent					= (r == 1 ? fitter.StopLossPercent		: StopLossPercent),
								TrailStopPercent				= (r == 1 ? fitter.TrailStopPercent		: TrailStopPercent),
								TrendStrength					= (r == 2 ? fitter.TrendStrength		: TrendStrength),
								UniversalOptimizer				= UniversalOptimizer,
							};

			if (!ret.IsConsistent)
				throw new InvalidOperationException("NewCrossOver");

			return ret;
		}

		internal Universal NewMutation(Random random)
		{
			// 0: entry
			// 1: exit
			// 2: exit on session close
			// 3: trend strength
			int						r			= random.Next(3);

			// 0: long
			// 1: short
			int						r2			= random.Next(2);

			List<IExpression>		expressions;
			Universal				ret			= new Universal { UniversalOptimizer = UniversalOptimizer };

			if (r == 0 && r2 == 0 && EnterLongCondition != null)
			{
				expressions						= EnterLongCondition.GetExpressions();
				ret.EnterLongCondition			= EnterLongCondition.NewMutation(this, random, expressions[random.Next(expressions.Count)]);
			}
			else
				ret.EnterLongCondition			= (EnterLongCondition != null ? EnterLongCondition.Clone() as IExpression : null);

			if (r == 0 && r2 == 1 && EnterShortCondition != null)
			{
				expressions						= EnterShortCondition.GetExpressions();
				ret.EnterShortCondition			= EnterShortCondition.NewMutation(this, random, expressions[random.Next(expressions.Count)]);
			}
			else
				ret.EnterShortCondition			= (EnterShortCondition != null ? EnterShortCondition.Clone() as IExpression : null);

			if (r == 1 && r2 == 0 && ExitLongCondition != null)
			{
				expressions						= ExitLongCondition.GetExpressions();
				ret.ExitLongCondition			= ExitLongCondition.NewMutation(this, random, expressions[random.Next(expressions.Count)]);
			}
			else
				ret.ExitLongCondition			= (ExitLongCondition != null ? ExitLongCondition.Clone() as IExpression : null);

			if (r == 1 && r2 == 1 && ExitShortCondition != null)
			{
				expressions						= ExitShortCondition.GetExpressions();
				ret.ExitShortCondition			= ExitShortCondition.NewMutation(this, random, expressions[random.Next(expressions.Count)]);
			}
			else
				ret.ExitShortCondition			= (ExitShortCondition != null ? ExitShortCondition.Clone() as IExpression : null);

			if (r == 1 && !double.IsNaN(ParabolicStopPercent))
				ret.ParabolicStopPercent		= ParabolicStopPercent * (random.Next(2) == 0 ? 0.75 : 1.25);
			else
				ret.ParabolicStopPercent		= ParabolicStopPercent;

			if (r == 1 && !double.IsNaN(ProfitTargetPercent))
				ret.ProfitTargetPercent			= ProfitTargetPercent * (random.Next(2) == 0 ? 0.75 : 1.25);
			else
				ret.ProfitTargetPercent			= ProfitTargetPercent;

			if (r == 1 && !double.IsNaN(StopLossPercent))
				ret.StopLossPercent				= StopLossPercent * (random.Next(2) == 0 ? 0.75 : 1.25);
			else
				ret.StopLossPercent				= StopLossPercent;

			if (r == 1 && !double.IsNaN(TrailStopPercent))
				ret.TrailStopPercent			= TrailStopPercent * (random.Next(2) == 0 ? 0.75 : 1.25);
			else
				ret.TrailStopPercent			= TrailStopPercent;

			if (r == 2 && UniversalOptimizer.OptimizeSessionClose)
				ret.ExitOnSessionClose			= random.Next(2) == 0;
			else
				ret.ExitOnSessionClose			= ExitOnSessionClose;

			if (r == 3)
				ret.TrendStrength				= 2 + random.Next(9);			// mutate between 2 and 10
			else
				ret.TrendStrength				= TrendStrength;

			if (!ret.IsConsistent)
				throw new InvalidOperationException("NewCrossOver");

			return ret;
		}

		internal Universal NewRandom(Random random)
		{
			// 0: long and short
			// 1: long only
			// 2: short only
			int			r							= (UniversalOptimizer.OptimizeEntries ? random.Next(3) : -1);

			// Exits are a bit tricky. We don't want to mix different forms of exists but keep them separate. Feel free to try a different logic as you see need and fit...
			// 0: Exit by 'condition' (indicator or candle stick, no other stop/target set)
			// 1: ParabolicStop (no ProfitTarget set)
			// 2: StopLoss + ProfitTarget
			// 3: TrailStop + ProfitTarget
			int			r2							= (!UniversalOptimizer.OptimizeExits
															|| (!UniversalOptimizer.UseCandleStickPatternForExits
																&& !UniversalOptimizer.UseIndicatorsForExits
																&& !UniversalOptimizer.UseParabolicStopForExits
																&& !UniversalOptimizer.UseStopTargetsForExits)
														? -1 
														: random.Next((UniversalOptimizer.UseCandleStickPatternForExits || UniversalOptimizer.UseIndicatorsForExits ? 1 : 0)
																	+ (UniversalOptimizer.UseParabolicStopForExits ? 1 : 0)
																	+ (UniversalOptimizer.UseStopTargetsForExits ? 2 : 0))
																+ (UniversalOptimizer.UseCandleStickPatternForExits || UniversalOptimizer.UseIndicatorsForExits ? 0 : 1)
																+ (UniversalOptimizer.UseParabolicStopForExits ? 0 : 1));

			double		initialStopTargetPercent	= stopTargetPercentStep * (1 + random.Next(8));

			Universal	ret = new Universal
						{
							EnterLongCondition		= ((r == 0 || r == 1)						? RandomExpression(random, true)	: null),
							EnterShortCondition		= ((r == 0 || r == 2)						? RandomExpression(random, true)	: null),
							ExitLongCondition		= (r2 == 0 && (r == 0 || r == 1)			? RandomExpression(random, false)	: null),
							ExitShortCondition		= (r2 == 0 && (r == 0 || r == 2)			? RandomExpression(random, false)	: null),
							ExitOnSessionClose		= (UniversalOptimizer.OptimizeSessionClose	? new bool?(random.Next(2) == 0)	: new bool?()),
							ParabolicStopPercent	= (r2 == 1									? initialStopTargetPercent			: double.NaN),
							ProfitTargetPercent		= (r2 != 0 && r2 != 1						? 2 * initialStopTargetPercent		: double.NaN),
							StopLossPercent			= (r2 == 2									? initialStopTargetPercent			: double.NaN),
							TrailStopPercent		= (r2 == 3									? initialStopTargetPercent			: double.NaN),
							TrendStrength			= 2 + random.Next(9),						// mutate between 2 and 10)
							UniversalOptimizer		= UniversalOptimizer,
						};

			if (!ret.IsConsistent)
				throw new InvalidOperationException("NewCrossOver");

			return ret;
		}

		internal int NumNodes
		{
			get
			{
				if (numNodes >= 0)
					return numNodes;

				lock (syncRoot)
				{
					if (numNodes >= 0)
						return numNodes;

					numNodes = 0;
					numNodes += (EnterLongCondition		!= null ? EnterLongCondition.GetExpressions().Count		: 0);
					numNodes += (EnterShortCondition	!= null ? EnterShortCondition.GetExpressions().Count	: 0);
					numNodes += (ExitLongCondition		!= null ? ExitLongCondition.GetExpressions().Count		: 0);
					numNodes += (ExitShortCondition		!= null ? ExitShortCondition.GetExpressions().Count		: 0);

					numNodes += (!double.IsNaN(ParabolicStopPercent)	? 1 : 0);
					numNodes += (!double.IsNaN(ProfitTargetPercent)		? 1 : 0);
					numNodes += (!double.IsNaN(StopLossPercent)			? 1 : 0);
					numNodes += (!double.IsNaN(TrailStopPercent)		? 1 : 0);

					return numNodes;
				}
			}
		}

		/// <summary>
		/// Called on every OnBarUpdate. Implement your custom logic here.
		/// </summary>
		/// <param name="strategy"></param>
		public override void OnBarUpdate(StrategyBase strategy)
		{
			if (strategy.CurrentBars[0] < strategy.BarsRequiredToTrade)
				return;

			if (Interlocked.CompareExchange(ref isInitialized, 1, 0) == 0)
			{
				if (EnterLongCondition	!= null)	EnterLongCondition.Initialize(strategy);
				if (EnterShortCondition	!= null)	EnterShortCondition.Initialize(strategy);
				if (ExitLongCondition	!= null)	ExitLongCondition.Initialize(strategy);
				if (ExitShortCondition	!= null)	ExitShortCondition.Initialize(strategy);

				if (!double.IsNaN(ParabolicStopPercent))
					strategy.SetParabolicStop(CalculationMode.Percent, ParabolicStopPercent);

				if (!double.IsNaN(ProfitTargetPercent))
					strategy.SetProfitTarget(CalculationMode.Percent, ProfitTargetPercent);

				if (!double.IsNaN(StopLossPercent))
					strategy.SetStopLoss(CalculationMode.Percent, StopLossPercent);

				if (!double.IsNaN(TrailStopPercent))
					strategy.SetTrailStop(CalculationMode.Percent, TrailStopPercent);
			}

			if (EnterLongCondition != null && EnterLongCondition.Evaluate(this, strategy) == true)
				strategy.EnterLong();
			
			if (ExitLongCondition != null && ExitLongCondition.Evaluate(this, strategy) == true)
				strategy.ExitLong();

			if (EnterShortCondition != null && EnterShortCondition.Evaluate(this, strategy) == true)
				strategy.EnterShort();
			
			if (ExitShortCondition != null && ExitShortCondition.Evaluate(this, strategy) == true)
				strategy.ExitShort();
		}

		/// <summary>
		/// Called on every OnStateChange. Implement your custom logic here.
		/// </summary>
		/// <param name="strategy"></param>
		public override void OnStateChange(StrategyBase strategy)
		{
			if (strategy.State == State.Configure && ExitOnSessionClose.HasValue)
				strategy.IsExitOnSessionCloseStrategy = ExitOnSessionClose.Value;

		}

		public double ParabolicStopPercent
		{ get; set; }

		public double ProfitTargetPercent
		{ get; set; }

		internal IExpression RandomExpression(Random random, bool? isEntry = null)
		{
			bool useCandleStickPattern	= isEntry == null || (isEntry == true && UniversalOptimizer.UseCandleStickPatternForEntries)	|| (isEntry == false && UniversalOptimizer.UseCandleStickPatternForExits);
			bool useIndicators			= isEntry == null || (isEntry == true && UniversalOptimizer.UseIndicatorsForEntries)			|| (isEntry == false && UniversalOptimizer.UseIndicatorsForExits);

			int r = random.Next(1 + (useCandleStickPattern ? 2 : 0) + (useIndicators ? 2 : 0));

			if (!useCandleStickPattern && !useIndicators)
				return null;
			else if (r == 0)
				return new LogicalExpression { Left = RandomExpression(random, isEntry), Operator = (LogicalOperator) random.Next(NumLogicalOperators), Right = RandomExpression(random, isEntry) };
			else if (useCandleStickPattern && r <= 2)
				return new CandleStickPatternExpression { Pattern = (ChartPattern) random.Next(Universal.NumChartPattern) };
			else
			{
				// .Right is not needed in all cases. However, it makes our lives easier if it's set
				IndicatorExpression ret = new IndicatorExpression {
													ComparePercent		= random.Next(101) / 100.0,
													Condition			= (Condition) random.Next(NumConditions),
													Left				= RandomIndicator(random),
													LeftBarsAgo			= 0,
													Right				= RandomIndicator(random),
													RightBarsAgo		= 0,
													UsePriceToCompare	= (random.Next(2) == 0) };

				if (ret.Left.IsOverlay)
					while (!ret.Right.IsOverlay)
					{
						try { ret.Right.SetState(NinjaTrader.NinjaScript.State.Finalized); } catch {}
						ret.Right = RandomIndicator(random);
					}
				else
					while (ret.Right.IsOverlay)
					{
						try { ret.Right.SetState(NinjaTrader.NinjaScript.State.Finalized); } catch {}
						ret.Right = RandomIndicator(random);
					}

				// make sure .State >= .Configure
				try
				{
					ret.Left.SetState(NinjaTrader.NinjaScript.State.Configure);
				}
				catch (Exception exp)
				{
					Cbi.Log.Process(typeof(Resource), "CbiUnableToCreateInstance2", new object[] { ret.Left.Name, exp.InnerException != null ? exp.InnerException.ToString() : exp.ToString() }, Cbi.LogLevel.Error, Cbi.LogCategories.Default);
					ret.Left.SetState(NinjaTrader.NinjaScript.State.Finalized);
					return null;
				}

				try
				{
					ret.Right.SetState(NinjaTrader.NinjaScript.State.Configure);
				}
				catch (Exception exp)
				{
					Cbi.Log.Process(typeof(Resource), "CbiUnableToCreateInstance2", new object[] { ret.Right.Name, exp.InnerException != null ? exp.InnerException.ToString() : exp.ToString() }, Cbi.LogLevel.Error, Cbi.LogCategories.Default);
					ret.Right.SetState(NinjaTrader.NinjaScript.State.Finalized);
					return null;
				}

				return ret;
			}
		}

		internal IndicatorBase RandomIndicator(Random random)
		{
			IndicatorBase	ret;
			Type			type = null;
			while (true)
			{
				try
				{
					type	= UniversalOptimizer.IndicatorTypes.Keys.ToList()[random.Next(UniversalOptimizer.IndicatorTypes.Count - 1)];
					ret		= (IndicatorBase) type.Assembly.CreateInstance(type.FullName);
					ret.SetState(NinjaTrader.NinjaScript.State.Configure);

					if (!ret.VerifyVendorLicense()			// make sure to not run into vendor license violations
						|| ret.BarsPeriods.Length > 1		// multi-series are not supported (yet)
						|| ret.Values.Length == 0)			// needed to have at least one plot
					{
						try { ret.SetState(NinjaTrader.NinjaScript.State.Finalized); } catch {}
						continue;
					}

					return ret;
				}
				catch (Exception exp)
				{
					Cbi.Log.Process(typeof(Resource), "CbiUnableToCreateInstance2", new object[] { type.FullName, exp.InnerException != null ? exp.InnerException.ToString() : exp.ToString() }, Cbi.LogLevel.Error, Cbi.LogCategories.Default);
				}
			}
		}

		public double StopLossPercent
		{ get; set; }

		/// <summary>
		/// Create a hard coded versoin of the strategy.
		/// </summary>
		/// <param name="strategyName">Optional strategy name</param>
		/// <returns>The strategy code</returns>
		public override string ToString(string strategyName = null)
		{
			StringBuilder s = new StringBuilder();

			s.Append("//" + Environment.NewLine);
			s.Append("// Copyright (C) 2018, NinjaTrader LLC <www.ninjatrader.com>." + Environment.NewLine);
			s.Append("// NinjaTrader reserves the right to modify or overwrite this NinjaScript component with each release." + Environment.NewLine);
			s.Append("//" + Environment.NewLine);
			s.Append("#region Using declarations" + Environment.NewLine);
			s.Append("using System;" + Environment.NewLine);
			s.Append("using System.Collections.Generic;" + Environment.NewLine);
			s.Append("using System.ComponentModel;" + Environment.NewLine);
			s.Append("using System.ComponentModel.DataAnnotations;" + Environment.NewLine);
			s.Append("using System.Linq;" + Environment.NewLine);
			s.Append("using System.Text;" + Environment.NewLine);
			s.Append("using System.Threading.Tasks;" + Environment.NewLine);
			s.Append("using System.Windows;" + Environment.NewLine);
			s.Append("using System.Windows.Input;" + Environment.NewLine);
			s.Append("using System.Windows.Media;" + Environment.NewLine);
			s.Append("using System.Xml.Serialization;" + Environment.NewLine);
			s.Append("using NinjaTrader.Cbi;" + Environment.NewLine);
			s.Append("using NinjaTrader.Gui;" + Environment.NewLine);
			s.Append("using NinjaTrader.Gui.Chart;" + Environment.NewLine);
			s.Append("using NinjaTrader.Gui.SuperDom;" + Environment.NewLine);
			s.Append("using NinjaTrader.Data;" + Environment.NewLine);
			s.Append("using NinjaTrader.NinjaScript;" + Environment.NewLine);
			s.Append("using NinjaTrader.Core.FloatingPoint;" + Environment.NewLine);
			s.Append("using NinjaTrader.NinjaScript.Indicators;" + Environment.NewLine);
			s.Append("using NinjaTrader.NinjaScript.DrawingTools;" + Environment.NewLine);
			s.Append("#endregion" + Environment.NewLine + Environment.NewLine);

			s.Append("// This namespace holds strategies in this folder and is required. Do not change it." + Environment.NewLine);
			s.Append("namespace NinjaTrader.NinjaScript.Strategies" + Environment.NewLine);
			s.Append("{" + Environment.NewLine);
			s.Indent(1);		s.Append("public class " + (strategyName ?? "UniversalStrategy") + " : " + (strategyName ?? "Strategy") + Environment.NewLine);
			s.Indent(1);		s.Append("{" + Environment.NewLine);
			s.Indent(2);		s.Append("private Indicators.CandleStickPatternLogic candleStickPatternLogic;" + Environment.NewLine + Environment.NewLine);

			s.Indent(2);		s.Append("protected override void OnStateChange()" + Environment.NewLine);
			s.Indent(2);		s.Append("{" + Environment.NewLine);
			s.Indent(3);		s.Append("base.OnStateChange();" + Environment.NewLine + Environment.NewLine);
			s.Indent(3);		s.Append("if (State == State.SetDefaults)" + Environment.NewLine);
			s.Indent(3);		s.Append("{" + Environment.NewLine);
			s.Indent(4);		s.Append("IncludeTradeHistoryInBacktest             = false;" + Environment.NewLine);
			if (ExitOnSessionClose.HasValue)
				{ s.Indent(4);	s.Append("IsExitOnSessionCloseStrategy              = " + ExitOnSessionClose.Value.ToString().ToLower() + ";" + Environment.NewLine); }
			s.Indent(4);		s.Append("IsInstantiatedOnEachOptimizationIteration = true;" + Environment.NewLine);
			s.Indent(4);		s.Append("MaximumBarsLookBack                       = MaximumBarsLookBack.TwoHundredFiftySix;" + Environment.NewLine);
			if (!string.IsNullOrEmpty(strategyName))
				s.Indent(4);	s.Append("Name                                      = \"" + strategyName + "\";" + Environment.NewLine);
			s.Indent(4);		s.Append("SupportsOptimizationGraph                 = false;" + Environment.NewLine);
			s.Indent(3);		s.Append("}" + Environment.NewLine);
			s.Indent(3);		s.Append("else if (State == State.Configure)" + Environment.NewLine);
			s.Indent(3);		s.Append("{" + Environment.NewLine);
			s.Indent(4);		s.Append("candleStickPatternLogic = new CandleStickPatternLogic(this, " + TrendStrength + ");" + Environment.NewLine);
			if (!double.IsNaN(ParabolicStopPercent))
				{ s.Indent(4);	s.Append("SetParabolicStop(CalculationMode.Percent, " + ParabolicStopPercent.ToString(CultureInfo.InvariantCulture) + ");" + Environment.NewLine); }
			if (!double.IsNaN(ProfitTargetPercent))
				{ s.Indent(4);	s.Append("SetProfitTarget(CalculationMode.Percent, " + ProfitTargetPercent.ToString(CultureInfo.InvariantCulture) + ");" + Environment.NewLine); }
			if (!double.IsNaN(StopLossPercent))
				{ s.Indent(4);	s.Append("SetStopLoss(CalculationMode.Percent, " + StopLossPercent.ToString(CultureInfo.InvariantCulture) + ");" + Environment.NewLine); }
			if (!double.IsNaN(TrailStopPercent))
				{ s.Indent(4);	s.Append("SetTrailStop(CalculationMode.Percent, " + TrailStopPercent.ToString(CultureInfo.InvariantCulture) + ");" + Environment.NewLine); }
			s.Indent(3);		s.Append("}" + Environment.NewLine);
			s.Indent(2);		s.Append("}" + Environment.NewLine + Environment.NewLine);

			s.Indent(2);		s.Append("protected override void OnBarUpdate()" + Environment.NewLine);
			s.Indent(2);		s.Append("{" + Environment.NewLine);
			s.Indent(3);		s.Append("base.OnBarUpdate();" + Environment.NewLine + Environment.NewLine);

			s.Indent(3);		s.Append("if (CurrentBars[0] < BarsRequiredToTrade)" + Environment.NewLine);
			s.Indent(4);		s.Append("return;" + Environment.NewLine + Environment.NewLine);

			bool additionalNewLine = false;
			if (EnterLongCondition != null)
			{
				s.Indent(3);	s.Append("if ("); EnterLongCondition.Print(s, 3 + 1); s.Append(")" + Environment.NewLine);
				s.Indent(4);	s.Append("EnterLong();" + Environment.NewLine);

				additionalNewLine = true;
			}

			if (ExitLongCondition != null)
			{
				if (additionalNewLine)
					s.Append(Environment.NewLine);

				s.Indent(3);	s.Append("if ("); ExitLongCondition.Print(s, 3 + 1); s.Append(")" + Environment.NewLine);
				s.Indent(4);	s.Append("ExitLong();" + Environment.NewLine);

				additionalNewLine = true;
			}

			if (EnterShortCondition != null)
			{
				if (additionalNewLine)
					s.Append(Environment.NewLine);

				s.Indent(3);	s.Append("if ("); EnterShortCondition.Print(s, 3 + 1); s.Append(")" + Environment.NewLine);
				s.Indent(4);	s.Append("EnterShort();" + Environment.NewLine);

				additionalNewLine = true;
			}

			if (ExitShortCondition != null)
			{
				if (additionalNewLine)
					s.Append(Environment.NewLine);

				s.Indent(3);	s.Append("if ("); ExitShortCondition.Print(s, 3 + 1); s.Append(")" + Environment.NewLine);
				s.Indent(4);	s.Append("ExitShort();" + Environment.NewLine);

				additionalNewLine = true;
			}

			s.Indent(2);		s.Append("}" + Environment.NewLine);
			s.Indent(1);		s.Append("}" + Environment.NewLine);
			s.Append("}" + Environment.NewLine);

			return s.ToString();
		}

		/// <summary>
		/// Serialize to XML
		/// </summary>
		/// <returns></returns>
		public override XElement ToXml()
		{
			XElement ret = new XElement(GetType().Name);

			// This node is mandatory. Make sure it holds the proper Type.FullName
			ret.Add(new XElement("ClassName", GetType().FullName));

			if (EnterLongCondition	!= null)	ret.Add(new XElement("EnterLongCondition",	EnterLongCondition.ToXml()));
			if (EnterShortCondition	!= null)	ret.Add(new XElement("EnterShortCondition",	EnterShortCondition.ToXml()));
			if (ExitLongCondition	!= null)	ret.Add(new XElement("ExitLongCondition",	ExitLongCondition.ToXml()));
			if (ExitShortCondition	!= null)	ret.Add(new XElement("ExitShortCondition",	ExitShortCondition.ToXml()));

			if (ExitOnSessionClose.HasValue)
				ret.Add(new XElement("ExitOnSessionClose",				ExitOnSessionClose.Value.ToString(CultureInfo.InvariantCulture)));

			ret.Add(new XElement("ParabolicStopPercent",				ParabolicStopPercent.ToString(CultureInfo.InvariantCulture)));
			ret.Add(new XElement("ProfitTargetPercent",					ProfitTargetPercent.ToString(CultureInfo.InvariantCulture)));
			ret.Add(new XElement("StopLossPercent",						StopLossPercent.ToString(CultureInfo.InvariantCulture)));
			ret.Add(new XElement("TrailStopPercent",					TrailStopPercent.ToString(CultureInfo.InvariantCulture)));
			ret.Add(new XElement("TrendStrength",						TrendStrength.ToString(CultureInfo.InvariantCulture)));

			return ret;
		}

		public int TrendStrength
		{ get; set; }

		public double TrailStopPercent
		{ get; set; }

		/// <summary>
		/// Constructor with no parameters is mandatory for any subclass of .UniversalBase
		/// </summary>
		public Universal()
		{
			ExitOnSessionClose				= new bool?();
			Id								= Interlocked.Increment(ref lastId);
			ParabolicStopPercent			= double.NaN;
			ProfitTargetPercent				= double.NaN;
			StopLossPercent					= double.NaN;
			TrailStopPercent				= double.NaN;
			TrendStrength					= 4;
		}

		public NinjaTrader.NinjaScript.Optimizers.UniversalOptimizer UniversalOptimizer
		{ get; set; }
	}
}

