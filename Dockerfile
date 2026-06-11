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

# Instalar dependencias nativas para WkHtmlToPdf
RUN apt-get update && apt-get install -y \
    libgdiplus \
    libjpeg62-turbo \
    libx11-6 \
    libxext6 \
    libxrender1 \
    libfontconfig1 \
    libfreetype6 \
    fontconfig \
    xfonts-75dpi \
    xfonts-base \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

ENTRYPOINT ["dotnet", "TechStore360.dll"]
