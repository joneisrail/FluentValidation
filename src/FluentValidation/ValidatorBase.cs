namespace FluentValidation {
	using System;
	using System.Collections;
	using System.Collections.Generic;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;
	using System.Runtime.CompilerServices;
	using System.Threading;
	using System.Threading.Tasks;
	using Internal;
	using Results;

	/// <summary>
	/// Base class for object validators.
	/// </summary>
	/// <typeparam name="T">The type of the object being validated</typeparam>
	public abstract class ValidatorBase<T> : IValidator<T>, IEnumerable<IValidationRule> {
		internal TrackingCollection<IValidationRule> NestedValidators { get; } = new TrackingCollection<IValidationRule>();
		private readonly bool _cacheEnabled;
		Func<CascadeMode> _cascadeMode = () => ValidatorOptions.CascadeMode;

		/// <summary>
		/// Sets the cascade mode for all rules within this validator.
		/// </summary>
		public CascadeMode CascadeMode {
			get => _cascadeMode();
			set => _cascadeMode = () => value;
		}

		/// <summary>
		/// Creates a new instance of the validator.
		/// </summary>
		protected ValidatorBase() : this(true) {
		}

		// todo: is a second constructor really the best way to do this?
		internal ValidatorBase(bool cacheEnabled) {
			_cacheEnabled = cacheEnabled;
			
			// Each time this validator is constructed
			// copy the rules out of the cache back into the local collection.
			// This also ensures the cache is initialized if this is the first instance
			// of this validator to be constructed.
			if (_cacheEnabled && RuleCache.TryGetRules(GetType(), out var rules)) {
				NestedValidators.AddRange(rules);
			}
		}
		
		/// <summary>
		/// Create your own validation rules in this method.
		/// </summary>
		protected abstract void Rules();

		protected IEnumerable<IValidationRule> GetRules() {
			// Don't directly return the result of RuleCache.GetRules.
			// If we do, then the result won't contain any rules added outside of the Rules method
			// Instead ensure the rules are copied from NestedValidators into the cache
			// But return NestedValidators directly as it will have the same data
			// (because it was initialized in the ctor)
			// plus any 'add hoc' rules.

			if (_cacheEnabled) {
				RuleCache.GetRules(GetType(), () => {
					Rules();
					// Copy the validators into the cache.
					// Don't cache the whole NestedValidators collection.
					return NestedValidators.ToList();
				});
			}

			return NestedValidators;
		}
		
		ValidationResult IValidator.Validate(object instance) {
			instance.Guard("Cannot pass null to Validate.", nameof(instance));
			if(! ((IValidator)this).CanValidateInstancesOfType(instance.GetType())) {
				throw new InvalidOperationException($"Cannot validate instances of type '{instance.GetType().Name}'. This validator can only validate instances of type '{typeof(T).Name}'.");
			}
			
			return Validate((T)instance);
		}
		
		Task<ValidationResult> IValidator.ValidateAsync(object instance, CancellationToken cancellation) {
			instance.Guard("Cannot pass null to Validate.", nameof(instance));
			if (!((IValidator) this).CanValidateInstancesOfType(instance.GetType())) {
				throw new InvalidOperationException($"Cannot validate instances of type '{instance.GetType().Name}'. This validator can only validate instances of type '{typeof(T).Name}'.");
			}

			return ValidateAsync((T) instance, cancellation);
		}
		
		ValidationResult IValidator.Validate(ValidationContext context) {
			context.Guard("Cannot pass null to Validate", nameof(context));
			return Validate(context.ToGeneric<T>());
		}
		
		Task<ValidationResult> IValidator.ValidateAsync(ValidationContext context, CancellationToken cancellation) {
			context.Guard("Cannot pass null to Validate", nameof(context));
			return ValidateAsync(context.ToGeneric<T>(), cancellation);
		}

		/// <summary>
		/// Validates the specified instance
		/// </summary>
		/// <param name="instance">The object to validate</param>
		/// <returns>A ValidationResult object containing any validation failures</returns>
		public ValidationResult Validate(T instance) {
			return Validate(new ValidationContext<T>(instance, new PropertyChain(), ValidatorOptions.ValidatorSelectors.DefaultValidatorSelectorFactory()));
		}

		/// <summary>
		/// Validates the specified instance asynchronously
		/// </summary>
		/// <param name="instance">The object to validate</param>
		/// <param name="cancellation">Cancellation token</param>
		/// <returns>A ValidationResult object containing any validation failures</returns>
		public Task<ValidationResult> ValidateAsync(T instance, CancellationToken cancellation = new CancellationToken()) {
			return ValidateAsync(new ValidationContext<T>(instance, new PropertyChain(), ValidatorOptions.ValidatorSelectors.DefaultValidatorSelectorFactory()), cancellation);
		}
		
		/// <summary>
		/// Validates the specified instance.
		/// </summary>
		/// <param name="context">Validation Context</param>
		/// <returns>A ValidationResult object containing any validation failures.</returns>
		public virtual ValidationResult Validate(ValidationContext<T> context) {
			context.Guard("Cannot pass null to Validate.", nameof(context));

			var result = new ValidationResult();
			bool shouldContinue = PreValidate(context, result);
			
			if (!shouldContinue) {
				return result;
			}

			EnsureInstanceNotNull(context.InstanceToValidate);
			
			var failures = GetRules().SelectMany(x => x.Validate(context));
			
			foreach (var validationFailure in failures.Where(failure => failure != null)) {
				result.Errors.Add(validationFailure);
			}

			return result;
		}

		/// <summary>
		/// Validates the specified instance asynchronously.
		/// </summary>
		/// <param name="context">Validation Context</param>
		/// <param name="cancellation">Cancellation token</param>
		/// <returns>A ValidationResult object containing any validation failures.</returns>
		public async virtual Task<ValidationResult> ValidateAsync(ValidationContext<T> context, CancellationToken cancellation = new CancellationToken()) {
			context.Guard("Cannot pass null to Validate", nameof(context));
			context.RootContextData["__FV_IsAsyncExecution"] = true;

			var result = new ValidationResult();
			
			bool shouldContinue = PreValidate(context, result);
			
			if (!shouldContinue) {
				return result;
			}

			EnsureInstanceNotNull(context.InstanceToValidate);

			foreach (var rule in GetRules()) {
				cancellation.ThrowIfCancellationRequested();
				var failures = await rule.ValidateAsync(context, cancellation);

				foreach (var failure in failures.Where(f => f != null)) {
					result.Errors.Add(failure);
				}
			}

			return result;
		}
		
		/// <summary>
		/// Adds a rule to the current validator.
		/// </summary>
		/// <param name="rule"></param>
		protected void AddRule(IValidationRule rule) {
			NestedValidators.Add(rule);
		}

		/// <summary>
		/// Creates a <see cref="IValidatorDescriptor" /> that can be used to obtain metadata about the current validator.
		/// </summary>
		public virtual IValidatorDescriptor CreateDescriptor() {
			return new ValidatorDescriptor<T>(GetRules());
		}

		bool IValidator.CanValidateInstancesOfType(Type type) {
			return typeof(T).GetTypeInfo().IsAssignableFrom(type.GetTypeInfo());
		}

		/// <summary>
		/// Defines a validation rule for a specify property.
		/// </summary>
		/// <example>
		/// RuleFor(x => x.Surname)...
		/// </example>
		/// <typeparam name="TProperty">The type of property being validated</typeparam>
		/// <param name="expression">The expression representing the property to validate</param>
		/// <returns>an IRuleBuilder instance on which validators can be defined</returns>
		public IRuleBuilderInitial<T, TProperty> RuleFor<TProperty>(Expression<Func<T, TProperty>> expression) {
			expression.Guard("Cannot pass null to RuleFor", nameof(expression));
			// If rule-level caching is enabled, then bypass the expression-level cache.
			// Otherwise we essentially end up caching expressions twice unnecessarily.
			bool bypassExpressionCache = _cacheEnabled;
			var rule = PropertyRule.Create(expression, () => CascadeMode, bypassExpressionCache);
			AddRule(rule);
			var ruleBuilder = new RuleBuilder<T, TProperty>(rule, this);
			return ruleBuilder;
		}

		/// <summary>
		/// Invokes a rule for each item in the collection
		/// </summary>
		/// <typeparam name="TProperty">Type of property</typeparam>
		/// <param name="expression">Expression representing the collection to validate</param>
		/// <returns>An IRuleBuilder instance on which validators can be defined</returns>
		public IRuleBuilderInitial<T, TProperty> RuleForEach<TProperty>(Expression<Func<T, IEnumerable<TProperty>>> expression) {
			expression.Guard("Cannot pass null to RuleForEach", nameof(expression));
			var rule = CollectionPropertyRule<TProperty>.Create(expression, () => CascadeMode);
			AddRule(rule);
			var ruleBuilder = new RuleBuilder<T, TProperty>(rule, this);
			return ruleBuilder;
		} 

		/// <summary>
		/// Defines a RuleSet that can be used to group together several validators.
		/// </summary>
		/// <param name="ruleSetName">The name of the ruleset.</param>
		/// <param name="action">Action that encapsulates the rules in the ruleset.</param>
		public void RuleSet(string ruleSetName, Action action) {
			ruleSetName.Guard("A name must be specified when calling RuleSet.", nameof(ruleSetName));
			action.Guard("A ruleset definition must be specified when calling RuleSet.", nameof(action));

			var ruleSetNames = ruleSetName.Split(',', ';')
				.Select(x => x.Trim())
				.ToArray();

			using (NestedValidators.OnItemAdded(r => r.RuleSets = ruleSetNames)) {
				action();
			}
		}

		/// <summary>
		/// Defines a condition that applies to several rules
		/// </summary>
		/// <param name="predicate">The condition that should apply to multiple rules</param>
		/// <param name="action">Action that encapsulates the rules.</param>
		/// <returns></returns>
		public void When(Func<T, bool> predicate, Action action) {
			var propertyRules = new List<IValidationRule>();

			Action<IValidationRule> onRuleAdded = propertyRules.Add;

			using(NestedValidators.OnItemAdded(onRuleAdded)) {
				action(); 
			}

			// Must apply the predicate after the rule has been fully created to ensure any rules-specific conditions have already been applied.
			propertyRules.ForEach(x => x.ApplyCondition(ctx => predicate((T)ctx.InstanceToValidate)));
		}
		
		/// <summary>
		/// Defines an inverse condition that applies to several rules
		/// </summary>
		/// <param name="predicate">The condition that should be applied to multiple rules</param>
		/// <param name="action">Action that encapsulates the rules</param>
		public void Unless(Func<T, bool> predicate, Action action) {
			When(x => !predicate(x), action);
		}

		/// <summary>
		/// Defines an asynchronous condition that applies to several rules
		/// </summary>
		/// <param name="predicate">The asynchronous condition that should apply to multiple rules</param>
		/// <param name="action">Action that encapsulates the rules.</param>
		/// <returns></returns>
		public void WhenAsync(Func<T, CancellationToken, Task<bool>> predicate, Action action) {
			var propertyRules = new List<IValidationRule>();

			Action<IValidationRule> onRuleAdded = propertyRules.Add;

			using (NestedValidators.OnItemAdded(onRuleAdded)) {
				action();
			}

			// Must apply the predicate after the rule has been fully created to ensure any rules-specific conditions have already been applied.
			propertyRules.ForEach(x => x.ApplyAsyncCondition((ctx, token) => predicate((T)ctx.InstanceToValidate, token)));
		}

		/// <summary>
		/// Defines an inverse asynchronous condition that applies to several rules
		/// </summary>
		/// <param name="predicate">The asynchronous condition that should be applied to multiple rules</param>
		/// <param name="action">Action that encapsulates the rules</param>
		public void UnlessAsync(Func<T, CancellationToken, Task<bool>> predicate, Action action) {
			WhenAsync(async (x, ct) => !await predicate(x, ct), action);
		}

		/// <summary>
		/// Includes the rules from the specified validator
		/// </summary>
		public void Include(IValidator<T> rulesToInclude) {
			rulesToInclude.Guard("Cannot pass null to Include", nameof(rulesToInclude));
			var rule = IncludeRule.Create<T>(rulesToInclude, () => CascadeMode);
			AddRule(rule);
		}
		
		/// <summary>
		/// Includes the rules from the specified validator
		/// </summary>
		public void Include<TValidator>(Func<T, TValidator> rulesToInclude) where TValidator : IValidator<T> {
			rulesToInclude.Guard("Cannot pass null to Include", nameof(rulesToInclude));
			var rule = IncludeRule.Create(rulesToInclude, () => CascadeMode);
			AddRule(rule);
		}

		/// <summary>
		/// Returns an enumerator that iterates through the collection of validation rules.
		/// </summary>
		/// <returns>
		/// A <see cref="T:System.Collections.Generic.IEnumerator`1"/> that can be used to iterate through the collection.
		/// </returns>
		/// <filterpriority>1</filterpriority>
		public IEnumerator<IValidationRule> GetEnumerator() {
			return GetRules().GetEnumerator();
		}

		IEnumerator IEnumerable.GetEnumerator() {
			return GetEnumerator();
		}

		/// <summary>
		/// Throws an exception if the instance being validated is null.
		/// </summary>
		/// <param name="instanceToValidate"></param>
		protected virtual void EnsureInstanceNotNull(object instanceToValidate) {
			instanceToValidate.Guard("Cannot pass null model to Validate.", nameof(instanceToValidate));
		}

		/// <summary>
		/// Determines if validation should occur and provides a means to modify the context and ValidationResult prior to execution.
		/// If this method returns false, then the ValidationResult is immediately returned from Validate/ValidateAsync.
		/// </summary>
		/// <param name="context"></param>
		/// <param name="result"></param>
		/// <returns></returns>
		protected virtual bool PreValidate(ValidationContext<T> context, ValidationResult result) {
			return true;
		}
	}
}