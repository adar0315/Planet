# 資料庫結構

Schema 以 `Planet.Data` 的 EF Core Migrations 為主，不手動維護 SQL 檔，避免與程式碼定義的 Model 不同步。

初次建立 migration：

```bash
dotnet ef migrations add InitialCreate --project ../../src/Planet.Data --startup-project ../../src/Planet.Service
```

套用到資料庫：

```bash
dotnet ef database update --project ../../src/Planet.Data --startup-project ../../src/Planet.Service
```

若需要純 SQL 部署腳本（例如給 DBA 審查），可用：

```bash
dotnet ef migrations script --project ../../src/Planet.Data --startup-project ../../src/Planet.Service -o schema.generated.sql
```
