cur = ${shell pwd | awk -F'/' '{print $$NF}'}
out = bin/Debug/net10.0
files = ${out}/${cur}.*
version = ${shell grep -oP '(?<=ModuleVersion => ")[0-9.]+' src/main.cs}
api = ../../api
shared_api = shared/permission_manager_api
config = ../../configs/plugins/permission_manager

.PHONY: all
all: api
	rm -f *.zip
	rm -f ${cur}.dll ${cur}.pdb ${cur}.deps.json
	dotnet build
	cp ${out}/${cur}.dll ${out}/${cur}.pdb ${out}/${cur}.deps.json .
	# Copy NuGet dependency DLLs not already provided by CS2 API
	for dll in ${out}/*.dll; do \
		name=$$(basename "$$dll"); \
		case "$$name" in \
			${cur}.dll|permission_manager_api.dll|CounterStrikeSharp.API.dll) ;; \
			*) if [ ! -f "${api}/$$name" ]; then cp "$$dll" .; fi ;; \
		esac; \
	done

.PHONY: api
api:
	$(MAKE) -C ${shared_api}

.PHONY: test
test: api
	dotnet test tests/permission_manager_tests/permission_manager_tests.csproj

.PHONY: release_full
release_full: all
	rm -f ${cur}_${version}.zip
	mkdir -p addons/counterstrikesharp/plugins/${cur}
	mkdir -p addons/counterstrikesharp/configs/plugins/${cur}
	mkdir -p addons/counterstrikesharp/shared/${cur}_api
	cp ${cur}.dll ${cur}.pdb ${cur}.deps.json addons/counterstrikesharp/plugins/${cur}/
	# Copy all extra DLLs (non-plugin) into the plugin dir
	for dll in *.dll; do \
		name=$$(basename "$$dll"); \
		case "$$name" in \
			${cur}.dll|permission_manager_api.dll) ;; \
			*) cp "$$dll" addons/counterstrikesharp/plugins/${cur}/ ;; \
		esac; \
	done
	cp ${shared_api}/${cur}_api.dll ${shared_api}/${cur}_api.pdb ${shared_api}/${cur}_api.deps.json addons/counterstrikesharp/shared/${cur}_api/
	# Ship the data files so a fresh install starts with a sensible map
	for f in ${config}/*.json; do \
		[ -e "$$f" ] && cp "$$f" addons/counterstrikesharp/configs/plugins/${cur}/; \
	done
	zip -r "${cur}_${version}.zip" addons/*
	rm -rf addons