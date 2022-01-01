// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.ComponentModel.DataAnnotations;

namespace FastTunnel.Api.Models
{
    public class GetTokenRequest
    {
        [Required]
        public string name { get; set; }

        [Required]
        public string password { get; set; }
    }
}
