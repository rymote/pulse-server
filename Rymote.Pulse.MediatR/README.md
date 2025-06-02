# Rymote.Pulse.MediatR

MediatR integration for the Rymote.Pulse framework, enabling CQRS patterns and simplified handler registration for real-time messaging applications.

## Overview

Rymote.Pulse.MediatR bridges the gap between Pulse's real-time messaging capabilities and MediatR's command/query handling patterns. It provides:
- Automatic mapping of MediatR requests to Pulse RPC endpoints
- Event-driven architecture with MediatR notifications
- Scoped dependency injection per message
- Access to PulseContext within MediatR handlers
- Simplified handler registration

## Installation

```bash
dotnet add package Rymote.Pulse.MediatR
```

## Key Benefits

- **Clean Architecture**: Separates transport concerns from business logic
- **CQRS Support**: Natural fit for Command Query Responsibility Segregation
- **Testability**: Handlers can be unit tested without WebSocket infrastructure
- **Dependency Injection**: Full support for scoped services per request
- **Type Safety**: Compile-time checking with strong typing
- **Minimal Boilerplate**: Single-line endpoint registration

## Quick Start

### 1. Configure Services

```csharp
using Rymote.Pulse.MediatR;

var builder = WebApplication.CreateBuilder(args);

// Add Pulse MediatR integration
builder.Services.AddPulseMediatR(options =>
{
    options.ConfigureMediatR = config =>
    {
        // Register all handlers from assembly
        config.RegisterServicesFromAssemblyContaining<Program>();
        
        // Add custom MediatR behaviors
        config.AddBehavior<IPipelineBehavior<,>, LoggingBehavior<,>>();
    };
});

// Add your business services
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IEmailService, EmailService>();
```

### 2. Configure Dispatcher

```csharp
var app = builder.Build();

// Get required services
var dispatcher = app.Services.GetRequiredService<PulseDispatcher>();
var serviceProvider = app.Services;

// Enable MediatR middleware
dispatcher.UseMediatR(serviceProvider);

// Map endpoints with single lines
dispatcher.MapMediatRRequest<GetUserRequest, UserResponse>("users.get");
dispatcher.MapMediatRRequest<CreateUserCommand, CreateUserResult>("users.create");
dispatcher.MapMediatRNotification<UserCreatedEvent>("users.created");
dispatcher.MapMediatRStreamRequest<StreamPricesRequest, PriceUpdate>("prices.stream");
```

### 3. Implement Handlers

#### Request/Response Handler

```csharp
public class GetUserRequest : IRequest<UserResponse>
{
    public int UserId { get; set; }
}

public class UserResponse
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Email { get; set; }
}

public class GetUserHandler : IRequestHandler<GetUserRequest, UserResponse>
{
    private readonly IUserRepository _userRepository;
    private readonly IPulseContextAccessor _pulseContext;
    
    public GetUserHandler(
        IUserRepository userRepository,
        IPulseContextAccessor pulseContext)
    {
        _userRepository = userRepository;
        _pulseContext = pulseContext;
    }
    
    public async Task<UserResponse> Handle(
        GetUserRequest request, 
        CancellationToken cancellationToken)
    {
        // Access Pulse context
        var context = _pulseContext.Context;
        context?.Logger.LogInfo($"Getting user {request.UserId}");
        
        // Business logic
        var user = await _userRepository.GetByIdAsync(request.UserId);
        if (user == null)
            throw new NotFoundException($"User {request.UserId} not found");
        
        return new UserResponse
        {
            Id = user.Id,
            Name = user.Name,
            Email = user.Email
        };
    }
}
```

#### Command Handler with Events

```csharp
public class CreateUserCommand : IRequest<CreateUserResult>
{
    public string Name { get; set; }
    public string Email { get; set; }
}

public class CreateUserResult
{
    public int UserId { get; set; }
    public bool Success { get; set; }
}

public class CreateUserHandler : IRequestHandler<CreateUserCommand, CreateUserResult>
{
    private readonly IUserRepository _userRepository;
    private readonly IMediator _mediator;
    private readonly IPulseContextAccessor _pulseContext;
    
    public CreateUserHandler(
        IUserRepository userRepository,
        IMediator mediator,
        IPulseContextAccessor pulseContext)
    {
        _userRepository = userRepository;
        _mediator = mediator;
        _pulseContext = pulseContext;
    }
    
    public async Task<CreateUserResult> Handle(
        CreateUserCommand command,
        CancellationToken cancellationToken)
    {
        // Create user
        var user = new User { Name = command.Name, Email = command.Email };
        await _userRepository.AddAsync(user);
        
        // Publish domain event
        await _mediator.Publish(new UserCreatedEvent 
        { 
            UserId = user.Id,
            UserName = user.Name 
        }, cancellationToken);
        
        // Broadcast to Pulse groups
        var context = _pulseContext.Context;
        if (context != null)
        {
            var adminGroup = context.ConnectionManager.GetOrCreateGroup("admins");
            await context.SendEventAsync(
                adminGroup,
                "user.created",
                new { UserId = user.Id, Name = user.Name }
            );
        }
        
        return new CreateUserResult 
        { 
            UserId = user.Id, 
            Success = true 
        };
    }
}
```

#### Notification Handler

```csharp
public class UserCreatedEvent : INotification
{
    public int UserId { get; set; }
    public string UserName { get; set; }
}

public class UserCreatedEventHandler : INotificationHandler<UserCreatedEvent>
{
    private readonly IEmailService _emailService;
    private readonly IPulseContextAccessor _pulseContext;
    
    public UserCreatedEventHandler(
        IEmailService emailService,
        IPulseContextAccessor pulseContext)
    {
        _emailService = emailService;
        _pulseContext = pulseContext;
    }
    
    public async Task Handle(
        UserCreatedEvent notification,
        CancellationToken cancellationToken)
    {
        // Send welcome email
        await _emailService.SendWelcomeEmailAsync(notification.UserId);
        
        // Log event
        var context = _pulseContext.Context;
        context?.Logger.LogInfo($"User {notification.UserName} created");
    }
}
```

#### Streaming Handler

```csharp
public class StreamPricesRequest : IStreamRequest<PriceUpdate>
{
    public string Symbol { get; set; }
}

public class PriceUpdate : IStreamItem
{
    public string Symbol { get; set; }
    public decimal Price { get; set; }
    public DateTime Timestamp { get; set; }
}

public class StreamPricesHandler : IStreamRequestHandler<StreamPricesRequest, PriceUpdate>
{
    private readonly IPriceService _priceService;
    
    public StreamPricesHandler(IPriceService priceService)
    {
        _priceService = priceService;
    }
    
    public async IAsyncEnumerable<PriceUpdate> Handle(
        StreamPricesRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var price in _priceService.StreamPricesAsync(request.Symbol))
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;
                
            yield return new PriceUpdate
            {
                Symbol = request.Symbol,
                Price = price,
                Timestamp = DateTime.UtcNow
            };
        }
    }
}
```

## Advanced Features

### Custom Pipeline Behaviors

```csharp
public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;
    
    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }
    
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var context = new ValidationContext<TRequest>(request);
        var failures = _validators
            .Select(v => v.Validate(context))
            .SelectMany(result => result.Errors)
            .Where(f => f != null)
            .ToList();
            
        if (failures.Count != 0)
            throw new ValidationException(failures);
            
        return await next();
    }
}
```

### Exception Handling

```csharp
public class ExceptionHandlingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IPulseContextAccessor _pulseContext;
    
    public ExceptionHandlingBehavior(IPulseContextAccessor pulseContext)
    {
        _pulseContext = pulseContext;
    }
    
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        try
        {
            return await next();
        }
        catch (ValidationException ex)
        {
            var context = _pulseContext.Context;
            context?.Logger.LogWarning($"Validation failed: {ex.Message}");
            throw new PulseException(PulseStatus.BAD_REQUEST, ex.Message);
        }
        catch (NotFoundException ex)
        {
            throw new PulseException(PulseStatus.NOT_FOUND, ex.Message);
        }
        catch (Exception ex)
        {
            var context = _pulseContext.Context;
            context?.Logger.LogError("Unhandled exception", ex);
            throw new PulseException(PulseStatus.INTERNAL_ERROR, "An error occurred");
        }
    }
}
```

### PulseContext Extensions

```csharp
// Send MediatR notification through Pulse
await context.PublishMediatRNotificationAsync(
    "user.activity",
    new UserActivityNotification { UserId = 123, Activity = "login" }
);

// Broadcast to group with MediatR
await context.PublishMediatRNotificationToGroupAsync(
    "premium-users",
    "feature.unlocked",
    new FeatureUnlockedNotification { Feature = "Advanced Analytics" }
);
```

## Integration Patterns

### 1. Command/Query Separation

```csharp
// Commands modify state
dispatcher.MapMediatRRequest<CreateOrderCommand, OrderId>("orders.create");
dispatcher.MapMediatRRequest<UpdateOrderCommand, bool>("orders.update");

// Queries read state
dispatcher.MapMediatRRequest<GetOrderQuery, OrderDto>("orders.get");
dispatcher.MapMediatRRequest<ListOrdersQuery, List<OrderDto>>("orders.list");
```

### 2. Event Sourcing

```csharp
public class OrderCommandHandler : IRequestHandler<PlaceOrderCommand, OrderId>
{
    private readonly IEventStore _eventStore;
    private readonly IMediator _mediator;
    
    public async Task<OrderId> Handle(PlaceOrderCommand command, CancellationToken ct)
    {
        var orderId = OrderId.New();
        var events = new[]
        {
            new OrderPlacedEvent(orderId, command.CustomerId),
            new OrderItemsAddedEvent(orderId, command.Items)
        };
        
        await _eventStore.AppendEventsAsync(orderId, events);
        
        foreach (var @event in events)
            await _mediator.Publish(@event, ct);
            
        return orderId;
    }
}
```

### 3. Saga/Process Manager

```csharp
public class OrderSaga : 
    INotificationHandler<OrderPlacedEvent>,
    INotificationHandler<PaymentCompletedEvent>,
    INotificationHandler<ShipmentSentEvent>
{
    private readonly ISagaStore _sagaStore;
    private readonly IMediator _mediator;
    
    public async Task Handle(OrderPlacedEvent notification, CancellationToken ct)
    {
        var saga = await _sagaStore.LoadAsync(notification.OrderId);
        saga.Handle(notification);
        
        if (saga.ShouldRequestPayment())
            await _mediator.Send(new RequestPaymentCommand(saga.OrderId));
            
        await _sagaStore.SaveAsync(saga);
    }
    
    // ... other handlers
}
```

## Testing

### Unit Testing Handlers

```csharp
[Test]
public async Task GetUserHandler_Should_Return_User()
{
    // Arrange
    var repository = new Mock<IUserRepository>();
    repository.Setup(x => x.GetByIdAsync(123))
        .ReturnsAsync(new User { Id = 123, Name = "John" });
        
    var handler = new GetUserHandler(repository.Object, null);
    
    // Act
    var response = await handler.Handle(
        new GetUserRequest { UserId = 123 },
        CancellationToken.None);
    
    // Assert
    Assert.AreEqual(123, response.Id);
    Assert.AreEqual("John", response.Name);
}
```

### Integration Testing

```csharp
[Test]
public async Task Should_Handle_User_Creation_Flow()
{
    // Arrange
    var services = new ServiceCollection();
    services.AddPulseMediatR(opt => opt.ConfigureMediatR = cfg =>
        cfg.RegisterServicesFromAssemblyContaining<CreateUserHandler>());
    
    var provider = services.BuildServiceProvider();
    var mediator = provider.GetRequiredService<IMediator>();
    
    // Act
    var result = await mediator.Send(new CreateUserCommand 
    { 
        Name = "John",
        Email = "john@example.com"
    });
    
    // Assert
    Assert.IsTrue(result.Success);
    Assert.Greater(result.UserId, 0);
}
```

## Performance Considerations

- **Handler Lifetime**: Handlers are resolved per-request with scoped lifetime
- **Async Patterns**: All handlers should be truly async to avoid blocking
- **Streaming**: Use IAsyncEnumerable for efficient streaming
- **Memory**: Scoped services are disposed after each request

## Troubleshooting

### Common Issues

1. **Handler Not Found**
   - Ensure handler is registered in MediatR configuration
   - Check namespace and assembly scanning
   - Verify request/response types match

2. **Dependency Injection Errors**
   - Register all required services
   - Check service lifetimes (scoped vs singleton)
   - Ensure IPulseContextAccessor is only used in scoped services

3. **Serialization Issues**
   - Ensure all types have parameterless constructors
   - Mark properties with appropriate MessagePack attributes
   - Check for circular references

## Dependencies

- .NET 9.0 or later
- Rymote.Pulse.Core
- MediatR (12.x or later)
- Microsoft.Extensions.DependencyInjection

## License

This project is licensed under the MIT License. 