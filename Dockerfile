FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["TechStore360.csproj", "./"]
RUN dotnet restore "TechStore360.csproj"
COPY . .
WORKDIR "/src/"
RUN dotnet build "TechStore360.csproj" -c Release -o /app/build
RUN dotnet publish "TechStore360.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

ENTRYPOINT ["dotnet", "TechStore360.dll"]
