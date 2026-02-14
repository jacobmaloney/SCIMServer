# SCIMServer - Enterprise SCIM 2.0 Implementation

A professional, open-source SCIM 2.0 server implementation built with ASP.NET Core and Blazor Server, designed for enterprise identity management and provisioning.

## Features

### Core SCIM 2.0 Support
- Full User resource implementation with enterprise extension
- Group resource management (repository layer ready)
- Standard SCIM operations: CREATE, READ, UPDATE (PUT/PATCH), DELETE
- SCIM-compliant error responses and metadata handling

### Security & Authentication
- JWT Bearer token authentication
- API token management with secure storage
- Configurable authentication settings
- Token expiration and activity tracking

### Web Interface
- Modern Blazor Server UI for administration
- User management dashboard
- API token generation and management
- Audit log viewing
- Configuration management
- User generation tools for testing

### Data Management
- SQL Server database backend
- Extensible schema with custom attributes
- Comprehensive audit logging
- Transaction support for data integrity

## Quick Start

### Prerequisites
- .NET 8.0 SDK or later
- SQL Server (LocalDB, Express, or full instance)
- Visual Studio 2022 or VS Code (optional)

### Installation

1. Clone the repository:
```bash
git clone https://github.com/yourusername/SCIMServer.git
cd SCIMServer
```

2. Build the solution:
```bash
dotnet build
```

3. Configure the database connection in `appsettings.json`:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\mssqllocaldb;Database=SCIMServer;Trusted_Connection=True;"
  }
}
```

4. Run the application:
```bash
cd src/SCIMServer.Web
dotnet run
```

5. Navigate to `https://localhost:5001` for the web interface

## API Documentation

### Base URL
```
https://your-server/scim/v2
```

### Authentication
Include the Bearer token in the Authorization header:
```
Authorization: Bearer <your-jwt-token>
```

### User Endpoints

#### List Users
```http
GET /scim/v2/Users
```

#### Get User
```http
GET /scim/v2/Users/{id}
```

#### Create User
```http
POST /scim/v2/Users
Content-Type: application/scim+json

{
  "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
  "userName": "john.doe@example.com",
  "name": {
    "givenName": "John",
    "familyName": "Doe"
  },
  "emails": [{
    "value": "john.doe@example.com",
    "type": "work",
    "primary": true
  }]
}
```

#### Update User (Full)
```http
PUT /scim/v2/Users/{id}
Content-Type: application/scim+json
```

#### Update User (Partial)
```http
PATCH /scim/v2/Users/{id}
Content-Type: application/scim+json

{
  "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
  "Operations": [{
    "op": "replace",
    "path": "name.givenName",
    "value": "Jane"
  }]
}
```

#### Delete User
```http
DELETE /scim/v2/Users/{id}
```

## Architecture

### Solution Structure
```
SCIMServer/
├── src/
│   ├── SCIMServer.Core/          # Domain models and business logic
│   ├── SCIMServer.DataAccess/    # Data access layer and repositories
│   ├── SCIMServer.Web/           # Web application and API
│   └── SCIMServer.Installer/     # Installation wizard
├── Database/                      # Database scripts
└── docker-compose.yml            # Docker configuration
```

### Technology Stack
- **Framework**: ASP.NET Core 8.0
- **UI**: Blazor Server
- **ORM**: Dapper
- **Database**: SQL Server
- **Authentication**: JWT Bearer tokens
- **API**: RESTful SCIM 2.0

## Configuration

### JWT Settings
Configure JWT authentication in `appsettings.json`:
```json
{
  "JwtConfig": {
    "SecretKey": "your-secret-key-here",
    "Issuer": "SCIMServer",
    "Audience": "SCIMServerAPI",
    "ExpirationMinutes": 60
  }
}
```

### Database Configuration
Connection string configuration:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "your-connection-string"
  }
}
```

### CORS Settings
Configure CORS for API access:
```json
{
  "Cors": {
    "AllowedOrigins": ["https://trusted-client.com"],
    "AllowedMethods": ["GET", "POST", "PUT", "PATCH", "DELETE"],
    "AllowedHeaders": ["Authorization", "Content-Type"]
  }
}
```

## Development

### Building from Source
```bash
# Clone repository
git clone https://github.com/yourusername/SCIMServer.git

# Restore dependencies
dotnet restore

# Build solution
dotnet build

# Run tests
dotnet test

# Run application
dotnet run --project src/SCIMServer.Web
```

### Database Migrations
```bash
# Create database
sqlcmd -S (localdb)\mssqllocaldb -i Database/CreateDatabase.sql

# Apply updates
sqlcmd -S (localdb)\mssqllocaldb -i UpdateDatabase.sql
```

## Docker Support

### Using Docker Compose
```bash
# Development environment
docker-compose -f docker-compose.dev.yml up

# Production environment
docker-compose up
```

### Building Docker Image
```bash
docker build -t scimserver .
docker run -p 5000:5000 scimserver
```

## Known Issues & Roadmap

### Current Limitations
- Groups API controller not yet implemented (repository ready)
- SCIM filtering not fully implemented
- No bulk operations endpoint
- Schema discovery endpoints missing

### Planned Features
- [ ] Complete Groups API implementation
- [ ] Full SCIM filtering support
- [ ] Bulk operations endpoint
- [ ] Schema and ResourceType discovery
- [ ] ServiceProviderConfig endpoint
- [ ] Enhanced security features (rate limiting, RBAC)
- [ ] Performance optimizations and caching
- [ ] Comprehensive test coverage
- [ ] OpenAPI/Swagger documentation

## Contributing

We welcome contributions! Please follow these guidelines:

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

### Code Style
- Follow C# coding conventions
- Use meaningful variable and method names
- Add XML documentation for public APIs
- Write unit tests for new features

## Security

### Reporting Security Issues
Please report security vulnerabilities to [security@example.com]

### Best Practices
- Always use HTTPS in production
- Configure strong JWT secret keys
- Implement proper CORS policies
- Enable audit logging
- Regular security updates

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Support

- **Documentation**: [Wiki](https://github.com/yourusername/SCIMServer/wiki)
- **Issues**: [GitHub Issues](https://github.com/yourusername/SCIMServer/issues)
- **Discussions**: [GitHub Discussions](https://github.com/yourusername/SCIMServer/discussions)

## Acknowledgments

- SCIM 2.0 specification by IETF
- ASP.NET Core team at Microsoft
- Dapper micro-ORM contributors
- Open source community

---

**Note**: This is an active development project. Features and APIs may change. For production use, please review security configurations and implement appropriate hardening measures.