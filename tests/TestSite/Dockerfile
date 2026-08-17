# Demo host for BaryoDev.Umbraco.ReadAloud: a stock Umbraco with the package added and nothing
# else, so anything that works here works because of the package rather than the site around it.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/BaryoDev.Umbraco.ReadAloud/ ./src/BaryoDev.Umbraco.ReadAloud/
# Central Package Management: TestSite.csproj declares PackageReferences without versions,
# so without this the restore fails with NU1015.
COPY tests/Directory.Packages.props ./tests/
COPY tests/TestSite/ ./tests/TestSite/
COPY README.md icon.png ./

# RazorCompileOnBuild, which the Umbraco project template turns off, because the published output
# carries no Razor runtime compiler: Umbraco only depends on
# Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation through its development mode package. Left off,
# the demo's one template renders nothing and the article 404s. RazorCompileOnPublish is not the
# flag that does it on this SDK: with only that set the view is copied but never generated, and
# the published TestSite.dll carries no compiled item for it.
#
# Safe here because the view binds to IPublishedContent rather than to a generated model, and
# appsettings.Production.json sets ModelsMode to Nothing to keep it that way.
RUN dotnet publish tests/TestSite/TestSite.csproj -c Release -o /app -p:RazorCompileOnBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./

# SQLite, so the demo carries no database dependency of its own.
RUN mkdir -p /app/umbraco/Data
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "TestSite.dll"]
