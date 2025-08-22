# EFCore-IDbContextFactory

# Serilog issue with write log to database SQL Server
+ Serilog write to SQL Server
+ Add Logs table after/before run Program.cs?

# Solution:

## Step 1: Run dotnet ef database update
```
D:\gtechsltn\EFCore-IDbContextFactory\src\EFCore-IDbContextFactory>dotnet ef database update
Build started...
Build succeeded.
An error occurred while accessing the Microsoft.Extensions.Hosting services. Continuing without the application service provider. Error: Cannot open database "EFCore-IDbContextFactory-Db" requested by the login. The login failed.
Login failed for user 'MANH\ADMIN'.
Applying migration '20250822100406_InitData'.
Done.

D:\gtechsltn\EFCore-IDbContextFactory\src\EFCore-IDbContextFactory>
```

## Step 2: Run Program.cs

D:\gtechsltn\EFCore-IDbContextFactory\src\EFCore-IDbContextFactory\Program.cs